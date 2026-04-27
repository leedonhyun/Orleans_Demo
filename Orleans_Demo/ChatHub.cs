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
        var previous = await sessionGrain.GetCurrent();
        await sessionGrain.BindConnection(Context.ConnectionId);
        _ = await sessionGrain.SetRoomIfConnectionMatch(Context.ConnectionId, normalizedRoomId);
        var connectionSession = _client.GetGrain<IConnectionSessionGrain>(Context.ConnectionId);
        await connectionSession.Upsert(normalizedUserId, normalizedRoomId);

        // Keep room participant state aligned with active SignalR session.
        // This closes a race where API join happens before hub join and stale-prune removed the user.
        var player = _client.GetGrain<IPlayerGrain>(normalizedUserId);
        var transition = await player.JoinRoom(normalizedRoomId);
        var changedRoom = transition.Changed;

        if (!string.IsNullOrWhiteSpace(previous.ConnectionId) && previous.ConnectionId != Context.ConnectionId)
        {
            if (!string.IsNullOrWhiteSpace(previous.RoomId))
            {
                _ = TryRemoveFromGroup(previous.ConnectionId, previous.RoomId);
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
            _ = TryRemoveFromGroup(Context.ConnectionId, previous.RoomId);
        }

        if (!await TryAddToGroupWithRetry(Context.ConnectionId, normalizedRoomId))
        {
            _logger.LogWarning(
                "JoinRoom degraded: failed to add connection to SignalR group. roomId={RoomId}, userId={UserId}, connectionId={ConnectionId}",
                normalizedRoomId,
                normalizedUserId,
                Context.ConnectionId);
        }

        if (changedRoom && !string.IsNullOrWhiteSpace(previous.RoomId))
        {
            await NotifyParticipantChanged(previous.RoomId, transition.PreviousRoomParticipants);
        }

        await NotifyParticipantChanged(normalizedRoomId, transition.CurrentRoomParticipants);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionSession = _client.GetGrain<IConnectionSessionGrain>(Context.ConnectionId);
        var session = await connectionSession.GetCurrent();
        var userId = session.UserId;

        if (!string.IsNullOrWhiteSpace(userId))
        {
            try
            {
                var sessionGrain = _client.GetGrain<IUserSessionGrain>(userId);
                var current = await sessionGrain.GetCurrent();

                // Only clear participant state when this exact connection is still the active session.
                if (string.Equals(current.ConnectionId, Context.ConnectionId, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(current.RoomId))
                    {
                        var player = _client.GetGrain<IPlayerGrain>(userId);
                        var leaveResult = await player.LeaveRoom();
                        if (leaveResult.Changed)
                        {
                            await NotifyParticipantChanged(leaveResult.RoomId, leaveResult.Participants);
                        }
                    }

                    _ = await sessionGrain.ClearIfConnectionMatch(Context.ConnectionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Disconnect cleanup failed. userId={UserId}, connectionId={ConnectionId}",
                    userId,
                    Context.ConnectionId);
            }
        }

        await connectionSession.Clear();

        await base.OnDisconnectedAsync(exception);
    }

    public async Task LeaveRoom(string roomId)
    {
        var normalizedRoomId = roomId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedRoomId))
        {
            return;
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, normalizedRoomId);

        var connectionSession = _client.GetGrain<IConnectionSessionGrain>(Context.ConnectionId);
        var session = await connectionSession.GetCurrent();
        if (string.IsNullOrWhiteSpace(session.UserId))
        {
            return;
        }

        var player = _client.GetGrain<IPlayerGrain>(session.UserId);
        var leaveResult = await player.LeaveRoom();

        var userSession = _client.GetGrain<IUserSessionGrain>(session.UserId);
        _ = await userSession.SetRoomIfConnectionMatch(Context.ConnectionId, string.Empty);
        await connectionSession.Upsert(session.UserId, string.Empty);

        if (leaveResult.Changed)
        {
            await NotifyParticipantChanged(leaveResult.RoomId, leaveResult.Participants);
        }
        else
        {
            await NotifyParticipantChanged(normalizedRoomId);
        }
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

        var notifier = _client.GetGrain<IChatNotifierGrain>($"chat-notifier:{roomId}");
        await notifier.NotifyTypingChanged(roomId, userId, isTyping);
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
        var notifier = _client.GetGrain<IChatNotifierGrain>($"chat-notifier:{roomId}");
        await notifier.NotifyMessageRead(roomId, userId, sequence);
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
            // Group cleanup can occasionally timeout for stale/disconnected connections.
            // Do not block JoinRoom on group-cleanup; give it a short budget.
            var removeTask = Groups.RemoveFromGroupAsync(connectionId, roomId);
            var completed = await Task.WhenAny(removeTask, Task.Delay(TimeSpan.FromMilliseconds(800)));
            if (completed != removeTask)
            {
                _logger.LogWarning(
                    "RemoveFromGroupAsync skipped after timeout budget. roomId={RoomId}, connectionId={ConnectionId}",
                    roomId,
                    connectionId);

                _ = removeTask.ContinueWith(t =>
                {
                    var _ = t.Exception;
                }, TaskContinuationOptions.OnlyOnFaulted);

                return;
            }

            await removeTask;
        }
        catch (Exception ex) when (ex is TaskCanceledException || ex is TimeoutException)
        {
            _logger.LogWarning(ex,
                "RemoveFromGroupAsync timed out. roomId={RoomId}, connectionId={ConnectionId}",
                roomId,
                connectionId);
        }
    }

    private async Task NotifyParticipantChanged(string roomId, int? participants = null)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return;
        }

        var resolvedParticipants = participants;
        if (!resolvedParticipants.HasValue)
        {
            var room = _client.GetGrain<IRoomGrain>(roomId);
            resolvedParticipants = await room.GetParticipantCount();
        }

        var notifier = _client.GetGrain<IChatNotifierGrain>($"chat-notifier:{roomId}");
        await notifier.NotifyParticipantChanged(roomId, resolvedParticipants.Value);
    }
}
