
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
using Microsoft.AspNetCore.Http;

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
    options.Schema.For<UserCredentialDocument>();
    options.Schema.For<RoomDocument>();
    options.Schema.For<RoomMemberDocument>();
    //    options.Schema.For<MyDocument1>();
    //    options.Schema.For<MyDocument2>();
});
const string forcedRedisConnectionString = "localhost:6379,abortConnect=false";

builder.Services.AddStackExchangeRedisCache(options =>
{
    var redisOptions = ConfigurationOptions.Parse(forcedRedisConnectionString, true);
    redisOptions.AbortOnConnectFail = false;
    redisOptions.ConnectRetry = Math.Max(redisOptions.ConnectRetry, 5);
    redisOptions.ConnectTimeout = Math.Max(redisOptions.ConnectTimeout, 10000);
    redisOptions.SyncTimeout = Math.Max(redisOptions.SyncTimeout, 10000);

    options.ConfigurationOptions = redisOptions;
    options.InstanceName = "orleans-demo:session:";
});

builder.Services.AddSession(options =>
{
    options.Cookie.Name = ".orleans.demo.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromHours(8);
});
var signalRBuilder = builder.Services.AddSignalR();

signalRBuilder.AddStackExchangeRedis(forcedRedisConnectionString, options =>
{
    var redisOptions = ConfigurationOptions.Parse(forcedRedisConnectionString, true);
    redisOptions.AbortOnConnectFail = false;
    redisOptions.ConnectRetry = Math.Max(redisOptions.ConnectRetry, 5);
    redisOptions.ConnectTimeout = Math.Max(redisOptions.ConnectTimeout, 10000);
    redisOptions.SyncTimeout = Math.Max(redisOptions.SyncTimeout, 10000);
    options.Configuration = redisOptions;
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSession();
app.MapHub<ChatHub>("/hubs/chat");

static (bool HasSession, bool IsMatch, string SessionUserId) GetSessionAuthStatus(HttpContext httpContext, string? requestedUserId)
{
    var normalizedUserId = requestedUserId?.Trim();
    if (string.IsNullOrWhiteSpace(normalizedUserId))
    {
        return (false, false, string.Empty);
    }

    var sessionUserId = httpContext.Session.GetString("auth.userId")?.Trim();
    if (string.IsNullOrWhiteSpace(sessionUserId))
    {
        return (false, false, string.Empty);
    }

    return (true, string.Equals(sessionUserId, normalizedUserId, StringComparison.Ordinal), sessionUserId);
}

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

app.MapPost("/api/chat/{roomId}/message", async (HttpContext httpContext, string roomId, ChatSendRequest request, IClusterClient client, IHubContext<ChatHub> hubContext) =>
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

    var authStatus = GetSessionAuthStatus(httpContext, userId);
    if (!authStatus.HasSession)
    {
        return Results.Unauthorized();
    }

    var effectiveUserId = authStatus.IsMatch ? userId! : authStatus.SessionUserId;

    var player = client.GetGrain<IPlayerGrain>(effectiveUserId);
    await player.JoinRoom(roomId);

    var room = client.GetGrain<IRoomGrain>(roomId);
    var savedMessage = await room.SendChat(effectiveUserId, message, clientMessageId);
    await hubContext.Clients.Group(roomId).SendAsync("ChatMessageReceived", savedMessage);

    await hubContext.Clients.Group(roomId).SendAsync("MessageAck", new
    {
        roomId,
        userId = effectiveUserId,
        clientMessageId = savedMessage.ClientMessageId,
        sequence = savedMessage.Sequence
    });

    var participants = await room.GetParticipantCount();
    await hubContext.Clients.Group(roomId).SendAsync("ParticipantChanged", new { roomId, participants });

    return Results.Ok(new { roomId, userId = effectiveUserId, sequence = savedMessage.Sequence, clientMessageId = savedMessage.ClientMessageId });
});

app.MapPost("/api/chat/{roomId}/file", async (HttpContext httpContext, HttpRequest request, string roomId, IClusterClient client, IHubContext<ChatHub> hubContext, IWebHostEnvironment env) =>
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

    var authStatus = GetSessionAuthStatus(httpContext, userId);
    if (!authStatus.HasSession)
    {
        return Results.Unauthorized();
    }

    var effectiveUserId = authStatus.IsMatch ? userId : authStatus.SessionUserId;

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

    var player = client.GetGrain<IPlayerGrain>(effectiveUserId);
    await player.JoinRoom(roomId);

    var room = client.GetGrain<IRoomGrain>(roomId);
    var savedMessage = await room.SendChat(effectiveUserId, message, string.IsNullOrWhiteSpace(clientMessageId) ? null : clientMessageId);
    await hubContext.Clients.Group(roomId).SendAsync("ChatMessageReceived", savedMessage);

    await hubContext.Clients.Group(roomId).SendAsync("MessageAck", new
    {
        roomId,
        userId = effectiveUserId,
        clientMessageId = savedMessage.ClientMessageId,
        sequence = savedMessage.Sequence
    });

    var participants = await room.GetParticipantCount();
    await hubContext.Clients.Group(roomId).SendAsync("ParticipantChanged", new { roomId, participants });

    return Results.Ok(new
    {
        roomId,
        userId = effectiveUserId,
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

app.MapPost("/api/auth/login", async (HttpContext httpContext, LoginRequest request, IQuerySession session) =>
{
    var userId = request.UserId?.Trim();
    var password = request.Password ?? string.Empty;

    if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(password))
    {
        return Results.BadRequest(new { message = "userId and password are required." });
    }

    var user = await session.Query<UserCredentialDocument>()
        .FirstOrDefaultAsync(x => x.UserId == userId && x.IsActive);

    if (user is null || !string.Equals(user.Password, password, StringComparison.Ordinal))
    {
        return Results.Unauthorized();
    }

    httpContext.Session.SetString("auth.userId", user.UserId);
    httpContext.Session.SetString("auth.displayName", string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserId : user.DisplayName);

    return Results.Ok(new
    {
        userId = user.UserId,
        displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserId : user.DisplayName
    });
});

app.MapPost("/api/auth/register", async (RegisterRequest request, IQuerySession querySession, IDocumentSession documentSession) =>
{
    var userId = request.UserId?.Trim();
    var password = request.Password ?? string.Empty;
    var displayName = request.DisplayName?.Trim() ?? string.Empty;

    if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(password))
    {
        return Results.BadRequest(new { message = "userId and password are required." });
    }

    var exists = await querySession.Query<UserCredentialDocument>()
        .AnyAsync(x => x.UserId == userId);

    if (exists)
    {
        return Results.Conflict(new { message = "userId already exists." });
    }

    var doc = new UserCredentialDocument
    {
        UserId = userId,
        Password = password,
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? userId : displayName,
        IsActive = true
    };

    documentSession.Store(doc);
    await documentSession.SaveChangesAsync();

    return Results.Created($"/api/users/{Uri.EscapeDataString(userId)}", new
    {
        userId = doc.UserId,
        displayName = doc.DisplayName
    });
});

app.MapGet("/api/lobby/rooms", async (IQuerySession session) =>
{
    var rooms = await session.Query<RoomDocument>()
        .Where(x => x.IsActive)
        .OrderBy(x => x.SortOrder)
        .ThenBy(x => x.RoomId)
        .ToListAsync();

    return Results.Ok(rooms.Select(x => new
    {
        roomId = x.RoomId,
        name = string.IsNullOrWhiteSpace(x.Name) ? x.RoomId : x.Name
    }));
});

app.MapGet("/api/lobby/rooms/{roomId}/users", async (string roomId, IQuerySession session) =>
{
    var normalizedRoomId = roomId?.Trim();
    if (string.IsNullOrWhiteSpace(normalizedRoomId))
    {
        return Results.BadRequest(new { message = "roomId is required." });
    }

    var memberUserIds = await session.Query<RoomMemberDocument>()
        .Where(x => x.RoomId == normalizedRoomId)
        .Select(x => x.UserId)
        .Distinct()
        .ToListAsync();

    if (memberUserIds.Count == 0)
    {
        return Results.Ok(Array.Empty<object>());
    }

    var users = await session.Query<UserCredentialDocument>()
        .Where(x => x.IsActive && memberUserIds.Contains(x.UserId))
        .OrderBy(x => x.UserId)
        .ToListAsync();

    return Results.Ok(users.Select(x => new
    {
        userId = x.UserId,
        displayName = string.IsNullOrWhiteSpace(x.DisplayName) ? x.UserId : x.DisplayName
    }));
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

public record LoginRequest(string UserId, string Password);

public record RegisterRequest(string UserId, string Password, string? DisplayName);

public class UserCredentialDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class RoomDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RoomId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class RoomMemberDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RoomId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
}