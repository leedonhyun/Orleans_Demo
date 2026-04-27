using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Http;
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
        if (string.IsNullOrWhiteSpace(normalizedRoomId))
        {
            return;
        }

        if (!TryResolveAuthorizedUserId(userId, out var normalizedUserId))
        {
            _logger.LogWarning("JoinRoom denied: missing or invalid session. connectionId={ConnectionId}, requestedUserId={RequestedUserId}",
                Context.ConnectionId,
                userId);
            return;
        }

        _logger.LogInformation("JoinRoom requested. connectionId={ConnectionId}, userId={UserId}, roomId={RoomId}",
            Context.ConnectionId,
            normalizedUserId,
            normalizedRoomId);

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

        _logger.LogInformation("JoinRoom completed. connectionId={ConnectionId}, userId={UserId}, roomId={RoomId}, participants={Participants}",
            Context.ConnectionId,
            normalizedUserId,
            normalizedRoomId,
            transition.CurrentRoomParticipants);
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
            _logger.LogWarning("LeaveRoom ignored: no connection session user. connectionId={ConnectionId}, roomId={RoomId}",
                Context.ConnectionId,
                normalizedRoomId);
            return;
        }

        if (!TryResolveAuthorizedUserId(session.UserId, out var effectiveUserId) || !string.Equals(effectiveUserId, session.UserId, StringComparison.Ordinal))
        {
            _logger.LogWarning("LeaveRoom denied: session user mismatch. connectionId={ConnectionId}, connectionUserId={ConnectionUserId}",
                Context.ConnectionId,
                session.UserId);
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
        if (!TryResolveAuthorizedUserId(userId, out var effectiveUserId))
        {
            _logger.LogWarning("NotifyTyping denied: invalid session. connectionId={ConnectionId}, requestedUserId={RequestedUserId}",
                Context.ConnectionId,
                userId);
            return;
        }

        if (!await IsActiveSession(effectiveUserId, roomId))
        {
            return;
        }

        var connectionSession = _client.GetGrain<IConnectionSessionGrain>(Context.ConnectionId);
        var changed = await connectionSession.SetTypingState(isTyping);
        if (!changed)
        {
            return;
        }

        var notifier = _client.GetGrain<IChatNotifierGrain>($"chat-notifier:{roomId}");
        await notifier.NotifyTypingChanged(roomId, effectiveUserId, isTyping);
    }

    public async Task NotifyRead(string roomId, string userId, long sequence)
    {
        if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(userId) || sequence <= 0)
        {
            return;
        }

        if (!TryResolveAuthorizedUserId(userId, out var effectiveUserId))
        {
            _logger.LogWarning("NotifyRead denied: invalid session. connectionId={ConnectionId}, requestedUserId={RequestedUserId}, roomId={RoomId}, sequence={Sequence}",
                Context.ConnectionId,
                userId,
                roomId,
                sequence);
            return;
        }

        if (!await IsActiveSession(effectiveUserId, roomId))
        {
            return;
        }

        var room = _client.GetGrain<IRoomGrain>(roomId);
        var changed = await room.MarkRead(effectiveUserId, sequence);
        if (!changed)
        {
            return;
        }

        var notifier = _client.GetGrain<IChatNotifierGrain>($"chat-notifier:{roomId}");
        await notifier.NotifyMessageRead(roomId, effectiveUserId, sequence);
    }

    private async Task<bool> IsActiveSession(string userId, string? roomId = null)
    {
        var sessionGrain = _client.GetGrain<IUserSessionGrain>(userId);
        var current = await sessionGrain.GetCurrent();
        if (!string.Equals(current.ConnectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(roomId) && !string.Equals(current.RoomId, roomId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private bool TryResolveAuthorizedUserId(string? requestedUserId, out string effectiveUserId)
    {
        effectiveUserId = string.Empty;

        var httpContext = Context.GetHttpContext();
        var sessionUserId = httpContext?.Session.GetString("auth.userId")?.Trim();
        if (string.IsNullOrWhiteSpace(sessionUserId))
        {
            return false;
        }

        var normalizedRequestedUserId = requestedUserId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(normalizedRequestedUserId)
            && !string.Equals(normalizedRequestedUserId, sessionUserId, StringComparison.Ordinal))
        {
            _logger.LogWarning("Hub userId overridden by session user. connectionId={ConnectionId}, requestedUserId={RequestedUserId}, sessionUserId={SessionUserId}",
                Context.ConnectionId,
                normalizedRequestedUserId,
                sessionUserId);
        }

        effectiveUserId = sessionUserId;
        return true;
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
