
using GrainInterfaces;
using GrainImplement;

using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.StaticFiles;


using Marten;

using Orleans.Configuration;
using StackExchange.Redis;

using System.Diagnostics;
using System.Text.Json;

using System.Net;

var builder = WebApplication.CreateBuilder(args);

var siloName = builder.Configuration["Silo"];
if (!string.IsNullOrWhiteSpace(siloName))
{
    builder.Configuration
        .AddJsonFile($"appsettings.{siloName}.json", optional: false, reloadOnChange: true)
        .AddJsonFile($"appsettings.{siloName}.Development.json", optional: true, reloadOnChange: true);

    var httpPort = siloName switch
    {
        "Silo1" => 5066,
        "Silo2" => 5067,
        "Silo3" => 5068,
        "Silo4" => 5069,
        "Silo5" => 5070,
        _ => 5066
    };

    builder.WebHost.UseUrls($"http://127.0.0.1:{httpPort}");
}

// Setup Orleans silo
builder.Host.UseOrleans(siloBuilder =>
{
    var siloPort = builder.Configuration.GetValue<int>("Orleans:SiloPort", 11111);
    var gatewayPort = builder.Configuration.GetValue<int>("Orleans:GatewayPort", 30000);
    var advertisedIpString = builder.Configuration.GetValue("Orleans:AdvertisedIPAddress", IPAddress.Loopback.ToString());
    var advertisedIp = IPAddress.TryParse(advertisedIpString, out var parsedIp)
        ? parsedIp
        : IPAddress.Loopback;

    siloBuilder.Configure<SiloMessagingOptions>(options =>
    {
        options.ResponseTimeout = TimeSpan.FromSeconds(600);
    });

    siloBuilder.Configure<ClientMessagingOptions>(options =>
    {
        options.ResponseTimeout = TimeSpan.FromSeconds(600);
    });

    siloBuilder.Configure<ClusterOptions>(options =>
    {
        options.ClusterId = "game-cluster";
        options.ServiceId = "game-service";
    });

    siloBuilder.Configure<EndpointOptions>(options =>
    {
        options.AdvertisedIPAddress = advertisedIp;
    });

    // Force load grain implementation assembly so Orleans can discover grain types.
    _ = typeof(PlayerGrain).Assembly;

    // Interflare.Orleans.Marten.Clustering:
    siloBuilder.UseMartenClustering();

    // Interflare.Orleans.Marten.Reminders:
    siloBuilder.UseMartenReminderService();

    // Interflare.Orleans.Marten.Persistence:
    siloBuilder.AddMartenGrainStorageAsDefault();
    // siloBuilder.AddMartenGrainStorage(name: "PubSubStore");

    siloBuilder.ConfigureEndpoints(
    siloPort: siloPort,
    gatewayPort: gatewayPort,
    listenOnAnyHostAddress: true
);
});


// Example Marten configuration
// see: https://martendb.io/configuration/hostbuilder.html
builder.Services.AddMarten(options =>
{
    options.Connection(builder.Configuration.GetConnectionString("MyDatabase"));
    options.DatabaseSchemaName = "incidents";
    //    options.Schema.For<MyDocument1>();
    //    options.Schema.For<MyDocument2>();
});
var signalRBuilder = builder.Services.AddSignalR();

var useSignalRRedis = builder.Configuration.GetValue<bool>("SignalR:Redis:Enabled");
if (useSignalRRedis)
{
    var signalRRedisConnectionString = builder.Configuration["SignalR:Redis:ConnectionString"]
        ?? builder.Configuration.GetConnectionString("SignalRRedis")
        ?? builder.Configuration.GetConnectionString("Redis");

    if (string.IsNullOrWhiteSpace(signalRRedisConnectionString))
    {
        throw new InvalidOperationException("SignalR Redis is enabled, but no Redis connection string was configured.");
    }

    signalRBuilder.AddStackExchangeRedis(signalRRedisConnectionString, options =>
    {
        var redisOptions = ConfigurationOptions.Parse(signalRRedisConnectionString, true);
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectRetry = Math.Max(redisOptions.ConnectRetry, 5);
        redisOptions.ConnectTimeout = Math.Max(redisOptions.ConnectTimeout, 10000);
        redisOptions.SyncTimeout = Math.Max(redisOptions.SyncTimeout, 10000);
        options.Configuration = redisOptions;
    });
}

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<ChatHub>("/hubs/chat");

var allowedUploadExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
    ".mp4", ".webm", ".mov", ".avi", ".mkv",
    ".txt",
    ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
};

const long maxUploadBytes = 15 * 1024 * 1024;

app.MapPost("/api/chat/{roomId}/join", async (string roomId, ChatJoinRequest request, IClusterClient client, IHubContext<ChatHub> hubContext) =>
{
    var userId = request.UserId?.Trim();
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.BadRequest(new { message = "userId is required." });
    }

    var player = client.GetGrain<IPlayerGrain>(userId);
    await player.JoinRoom(roomId);

    var room = client.GetGrain<IRoomGrain>(roomId);
    var participants = await room.GetParticipantCount();
    await hubContext.Clients.Group(roomId).SendAsync("ParticipantChanged", new { roomId, participants });

    return Results.Ok(new { roomId, userId });
});

app.MapPost("/api/chat/{roomId}/leave", async (string roomId, ChatLeaveRequest request, IClusterClient client, IHubContext<ChatHub> hubContext) =>
{
    var userId = request.UserId?.Trim();
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.BadRequest(new { message = "userId is required." });
    }

    var player = client.GetGrain<IPlayerGrain>(userId);
    await player.LeaveRoom();

    var room = client.GetGrain<IRoomGrain>(roomId);
    var participants = await room.GetParticipantCount();
    await hubContext.Clients.Group(roomId).SendAsync("ParticipantChanged", new { roomId, participants });

    return Results.Ok(new { roomId, userId });
});

app.MapPost("/api/chat/{roomId}/leave-keepalive", async (string roomId, string userId, IClusterClient client, IHubContext<ChatHub> hubContext) =>
{
    var resolvedUserId = userId?.Trim();
    if (string.IsNullOrWhiteSpace(resolvedUserId))
    {
        return Results.BadRequest(new { message = "userId is required." });
    }

    var player = client.GetGrain<IPlayerGrain>(resolvedUserId);
    await player.LeaveRoom();

    var room = client.GetGrain<IRoomGrain>(roomId);
    var participants = await room.GetParticipantCount();
    await hubContext.Clients.Group(roomId).SendAsync("ParticipantChanged", new { roomId, participants });

    return Results.Ok(new { roomId, userId = resolvedUserId });
});

app.MapPost("/api/chat/{roomId}/message", async (string roomId, ChatSendRequest request, IClusterClient client, IHubContext<ChatHub> hubContext) =>
{
    var userId = request.UserId?.Trim();
    var message = request.Message?.Trim();
    var clientMessageId = request.ClientMessageId?.Trim();

    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.BadRequest(new { message = "userId is required." });
    }

    if (string.IsNullOrWhiteSpace(message))
    {
        return Results.BadRequest(new { message = "message is required." });
    }

    var player = client.GetGrain<IPlayerGrain>(userId);
    await player.JoinRoom(roomId);

    var room = client.GetGrain<IRoomGrain>(roomId);
    var savedMessage = await room.SendChat(userId, message, clientMessageId);
    await hubContext.Clients.Group(roomId).SendAsync("ChatMessageReceived", savedMessage);

    await hubContext.Clients.Group(roomId).SendAsync("MessageAck", new
    {
        roomId,
        userId,
        clientMessageId = savedMessage.ClientMessageId,
        sequence = savedMessage.Sequence
    });

    var participants = await room.GetParticipantCount();
    await hubContext.Clients.Group(roomId).SendAsync("ParticipantChanged", new { roomId, participants });

    return Results.Ok(new { roomId, userId, sequence = savedMessage.Sequence, clientMessageId = savedMessage.ClientMessageId });
});

app.MapPost("/api/chat/{roomId}/file", async (HttpRequest request, string roomId, IClusterClient client, IHubContext<ChatHub> hubContext, IWebHostEnvironment env) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "multipart/form-data is required." });
    }

    var form = await request.ReadFormAsync();
    var userId = form["userId"].ToString().Trim();
    var clientMessageId = form["clientMessageId"].ToString().Trim();
    var file = form.Files["file"] ?? form.Files.FirstOrDefault();

    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.BadRequest(new { message = "userId is required." });
    }

    if (file is null || file.Length <= 0)
    {
        return Results.BadRequest(new { message = "file is required." });
    }

    if (file.Length > maxUploadBytes)
    {
        return Results.BadRequest(new { message = "file size must be <= 15MB." });
    }

    var extension = Path.GetExtension(file.FileName) ?? string.Empty;
    if (!allowedUploadExtensions.Contains(extension))
    {
        return Results.BadRequest(new { message = "unsupported file type." });
    }

    var safeOriginalName = Path.GetFileName(file.FileName);
    var webRoot = env.WebRootPath;
    if (string.IsNullOrWhiteSpace(webRoot))
    {
        webRoot = Path.Combine(env.ContentRootPath, "wwwroot");
    }

    var uploadDir = Path.Combine(webRoot, "uploads");
    Directory.CreateDirectory(uploadDir);

    var storedName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
    var savePath = Path.Combine(uploadDir, storedName);
    await using (var stream = File.Create(savePath))
    {
        await file.CopyToAsync(stream);
    }

    var fileUrl = "/api/chat/file/" + Uri.EscapeDataString(storedName);
    var payload = JsonSerializer.Serialize(new
    {
        url = fileUrl,
        name = safeOriginalName,
        contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
        size = file.Length
    });
    var message = "__file__:" + payload;

    var player = client.GetGrain<IPlayerGrain>(userId);
    await player.JoinRoom(roomId);

    var room = client.GetGrain<IRoomGrain>(roomId);
    var savedMessage = await room.SendChat(userId, message, string.IsNullOrWhiteSpace(clientMessageId) ? null : clientMessageId);
    await hubContext.Clients.Group(roomId).SendAsync("ChatMessageReceived", savedMessage);

    await hubContext.Clients.Group(roomId).SendAsync("MessageAck", new
    {
        roomId,
        userId,
        clientMessageId = savedMessage.ClientMessageId,
        sequence = savedMessage.Sequence
    });

    var participants = await room.GetParticipantCount();
    await hubContext.Clients.Group(roomId).SendAsync("ParticipantChanged", new { roomId, participants });

    return Results.Ok(new
    {
        roomId,
        userId,
        sequence = savedMessage.Sequence,
        clientMessageId = savedMessage.ClientMessageId,
        message = savedMessage.Message,
        sentAt = savedMessage.SentAt
    });
});

app.MapGet("/api/chat/file/{fileName}", (string fileName, IWebHostEnvironment env) =>
{
    var safeName = Path.GetFileName(fileName);
    if (string.IsNullOrWhiteSpace(safeName) || safeName != fileName)
    {
        return Results.BadRequest(new { message = "invalid file name." });
    }

    var webRoot = env.WebRootPath;
    if (string.IsNullOrWhiteSpace(webRoot))
    {
        webRoot = Path.Combine(env.ContentRootPath, "wwwroot");
    }

    var uploadDir = Path.Combine(webRoot, "uploads");
    var fullPath = Path.Combine(uploadDir, safeName);
    if (!File.Exists(fullPath))
    {
        return Results.NotFound(new { message = "file not found." });
    }

    var provider = new FileExtensionContentTypeProvider();
    if (!provider.TryGetContentType(fullPath, out var contentType))
    {
        contentType = "application/octet-stream";
    }

    return Results.File(fullPath, contentType, enableRangeProcessing: true);
});

var reactHandler = async (string roomId, ChatReactionRequest request, IClusterClient client, IHubContext<ChatHub> hubContext) =>
{
    var userId = request.UserId?.Trim();
    var emoji = request.Emoji?.Trim();

    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.BadRequest(new { message = "userId is required." });
    }

    if (string.IsNullOrWhiteSpace(emoji))
    {
        return Results.BadRequest(new { message = "emoji is required." });
    }

    if (request.Sequence <= 0)
    {
        return Results.BadRequest(new { message = "sequence must be greater than 0." });
    }

    var room = client.GetGrain<IRoomGrain>(roomId);
    var reactions = await room.ToggleReaction(request.Sequence, emoji, userId);

    await hubContext.Clients.Group(roomId).SendAsync("ReactionUpdated", new
    {
        roomId,
        sequence = request.Sequence,
        reactions
    });

    return Results.Ok(new { roomId, sequence = request.Sequence, reactions });
};

app.MapPost("/api/chat/{roomId}/react", reactHandler);
app.MapPost("/api/chat/{roomId}/reaction", reactHandler);

app.MapGet("/api/chat/{roomId}/messages", async (string roomId, IClusterClient client, int? take) =>
{
    var room = client.GetGrain<IRoomGrain>(roomId);
    var resolvedTake = Math.Clamp(take ?? 50, 1, 200);
    var messages = await room.GetRecentMessages(resolvedTake);
    var participants = await room.GetParticipantCount();
    var readReceipts = await room.GetReadReceipts();

    return Results.Ok(new
    {
        roomId,
        participants,
        messages,
        readReceipts
    });
});


//app.MapGet("/startchat", () => Results.Ok(new
//{
//    message = "chat started",
//    startedAt = DateTimeOffset.UtcNow
//}));
app.MapGet("/startchat/{roomId}", async (
    string roomId,
    IClusterClient client,
    int? totalPlayers,
    int? maxConcurrency,
    CancellationToken cancellationToken) =>
{
    var configuredTotalPlayers = app.Configuration.GetValue<int>("StartChat:TotalPlayers", 30_000);
    var resolvedTotalPlayers = totalPlayers ?? configuredTotalPlayers;
    if (resolvedTotalPlayers <= 0)
    {
        return Results.BadRequest(new { message = "totalPlayers must be greater than 0." });
    }

    var configuredMaxConcurrency = app.Configuration.GetValue<int>("StartChat:MaxConcurrency", 2_000);
    var resolvedMaxConcurrency = maxConcurrency ?? configuredMaxConcurrency;
    resolvedMaxConcurrency = Math.Clamp(resolvedMaxConcurrency, 1, resolvedTotalPlayers);

    var startedAt = DateTimeOffset.UtcNow;
    var stopwatch = Stopwatch.StartNew();

    await Parallel.ForEachAsync(
        Enumerable.Range(0, resolvedTotalPlayers),
        new ParallelOptions
        {
            MaxDegreeOfParallelism = resolvedMaxConcurrency,
            CancellationToken = cancellationToken
        },
        async (i, ct) =>
        {
            var player = client.GetGrain<IPlayerGrain>($"player-{i}");

            await player.JoinRoom(roomId);
            await player.SendInput("move_forward");
        });

    stopwatch.Stop();

    return Results.Ok(new
    {
        roomId,
        totalPlayers = resolvedTotalPlayers,
        maxConcurrency = resolvedMaxConcurrency,
        startedAt,
        elapsedMs = stopwatch.ElapsedMilliseconds
    });
});

app.Run();

public record ChatJoinRequest(string UserId);

public record ChatLeaveRequest(string UserId);

public record ChatSendRequest(string UserId, string Message, string? ClientMessageId);

public record ChatReactionRequest(string UserId, long Sequence, string Emoji);