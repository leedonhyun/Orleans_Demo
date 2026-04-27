namespace GrainImplement;

using GrainInterfaces;

using Orleans;

public class UserSessionState
{
    public string ConnectionId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
}

public class UserSessionGrain : Grain, IUserSessionGrain
{
    private readonly IPersistentState<UserSessionState> _state;

    public UserSessionGrain([PersistentState("user-session", "Default")] IPersistentState<UserSessionState> state)
    {
        _state = state;
    }

    public Task<UserSessionInfo> GetCurrent()
    {
        return Task.FromResult(new UserSessionInfo
        {
            ConnectionId = _state.State.ConnectionId,
            RoomId = _state.State.RoomId
        });
    }

    public async Task BindConnection(string connectionId)
    {
        var normalized = connectionId?.Trim() ?? string.Empty;
        if (string.Equals(_state.State.ConnectionId, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _state.State.ConnectionId = normalized;
        await _state.WriteStateAsync();
    }

    public async Task<bool> SetRoomIfConnectionMatch(string connectionId, string roomId)
    {
        var normalizedConnectionId = connectionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedConnectionId) || !string.Equals(_state.State.ConnectionId, normalizedConnectionId, StringComparison.Ordinal))
        {
            return false;
        }

        var normalizedRoomId = roomId?.Trim() ?? string.Empty;
        if (string.Equals(_state.State.RoomId, normalizedRoomId, StringComparison.Ordinal))
        {
            return false;
        }

        _state.State.RoomId = normalizedRoomId;
        await _state.WriteStateAsync();
        return true;
    }

    public async Task<bool> ClearIfConnectionMatch(string connectionId)
    {
        var normalizedConnectionId = connectionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedConnectionId) || !string.Equals(_state.State.ConnectionId, normalizedConnectionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_state.State.ConnectionId) && string.IsNullOrWhiteSpace(_state.State.RoomId))
        {
            return false;
        }

        _state.State.ConnectionId = string.Empty;
        _state.State.RoomId = string.Empty;
        await _state.WriteStateAsync();

        return true;
    }
}
