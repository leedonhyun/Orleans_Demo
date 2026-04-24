using Microsoft.AspNetCore.SignalR;
using Orleans;

using GrainInterfaces;

public class ChatHub : Hub
{
    private readonly IClusterClient _client;
    private readonly ILogger<ChatHub> _logger;

    private static readonly TimeSpan[] GroupOpRetryDelays =
    {
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(400)
    };

    public ChatHub(IClusterClient client, ILogger<ChatHub> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task JoinRoom(string roomId, string userId)
    {
        var normalizedRoomId = roomId?.Trim() ?? string.Empty;
        var normalizedUserId = userId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedRoomId) || string.IsNullOrWhiteSpace(normalizedUserId))
        {
            return;
        }

        var sessionGrain = _client.GetGrain<IUserSessionGrain>(normalizedUserId);
        var previous = await sessionGrain.Activate(Context.ConnectionId, normalizedRoomId);

        if (!string.IsNullOrWhiteSpace(previous.ConnectionId) && previous.ConnectionId != Context.ConnectionId)
        {
            if (!string.IsNullOrWhiteSpace(previous.RoomId))
            {
                await TryRemoveFromGroup(previous.ConnectionId, previous.RoomId);
            }

            await Clients.Client(previous.ConnectionId).SendAsync("ForceLogout", new
            {
                roomId = previous.RoomId,
                userId = normalizedUserId,
                reason = "같은 userId로 새로운 접속이 감지되어 기존 세션이 종료됩니다."
            });
        }
        else if (previous.ConnectionId == Context.ConnectionId && !string.IsNullOrWhiteSpace(previous.RoomId) && previous.RoomId != normalizedRoomId)
        {
            await TryRemoveFromGroup(Context.ConnectionId, previous.RoomId);
        }

        if (!await TryAddToGroupWithRetry(Context.ConnectionId, normalizedRoomId))
        {
            _logger.LogWarning(
                "JoinRoom degraded: failed to add connection to SignalR group. roomId={RoomId}, userId={UserId}, connectionId={ConnectionId}",
                normalizedRoomId,
                normalizedUserId,
                Context.ConnectionId);
        }
    }

    public Task LeaveRoom(string roomId)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
    }

    public Task NotifyTyping(string roomId, string userId, bool isTyping)
    {
        if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(userId))
        {
            return Task.CompletedTask;
        }

        return NotifyTypingCore(roomId, userId, isTyping);
    }

    private async Task NotifyTypingCore(string roomId, string userId, bool isTyping)
    {
        if (!await IsActiveSession(userId))
        {
            return;
        }

        await Clients.OthersInGroup(roomId).SendAsync("TypingChanged", new
        {
            roomId,
            userId,
            isTyping
        });
    }

    public async Task NotifyRead(string roomId, string userId, long sequence)
    {
        if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(userId) || sequence <= 0)
        {
            return;
        }

        if (!await IsActiveSession(userId))
        {
            return;
        }

        var room = _client.GetGrain<IRoomGrain>(roomId);
        await room.MarkRead(userId, sequence);

        await Clients.Group(roomId).SendAsync("MessageRead", new
        {
            roomId,
            userId,
            sequence
        });
    }

    private async Task<bool> IsActiveSession(string userId)
    {
        var sessionGrain = _client.GetGrain<IUserSessionGrain>(userId);
        var current = await sessionGrain.GetCurrent();
        return string.Equals(current.ConnectionId, Context.ConnectionId, StringComparison.Ordinal);
    }

    private async Task<bool> TryAddToGroupWithRetry(string connectionId, string roomId)
    {
        for (var attempt = 0; attempt <= GroupOpRetryDelays.Length; attempt++)
        {
            try
            {
                await Groups.AddToGroupAsync(connectionId, roomId);
                return true;
            }
            catch (Exception ex) when (ex is TaskCanceledException || ex is TimeoutException)
            {
                _logger.LogWarning(ex,
                    "AddToGroupAsync timed out. roomId={RoomId}, connectionId={ConnectionId}, attempt={Attempt}",
                    roomId,
                    connectionId,
                    attempt + 1);

                if (attempt >= GroupOpRetryDelays.Length || Context.ConnectionAborted.IsCancellationRequested)
                {
                    return false;
                }

                try
                {
                    await Task.Delay(GroupOpRetryDelays[attempt], Context.ConnectionAborted);
                }
                catch (TaskCanceledException)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private async Task TryRemoveFromGroup(string connectionId, string roomId)
    {
        try
        {
            await Groups.RemoveFromGroupAsync(connectionId, roomId);
        }
        catch (Exception ex) when (ex is TaskCanceledException || ex is TimeoutException)
        {
            _logger.LogWarning(ex,
                "RemoveFromGroupAsync timed out. roomId={RoomId}, connectionId={ConnectionId}",
                roomId,
                connectionId);
        }
    }
}
