namespace GrainImplement;

using GrainInterfaces;
using Orleans;

public class ConnectionSessionState
{
    public string UserId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public bool IsTyping { get; set; }
}

public class ConnectionSessionGrain : Grain, IConnectionSessionGrain
{
    private readonly IPersistentState<ConnectionSessionState> _state;

    public ConnectionSessionGrain([PersistentState("connection-session", "Default")] IPersistentState<ConnectionSessionState> state)
    {
        _state = state;
    }

    public async Task Upsert(string userId, string roomId)
    {
        var normalizedUserId = userId?.Trim() ?? string.Empty;
        var normalizedRoomId = roomId?.Trim() ?? string.Empty;
        var roomChanged = !string.Equals(_state.State.RoomId, normalizedRoomId, StringComparison.Ordinal);

        _state.State.UserId = normalizedUserId;
        _state.State.RoomId = normalizedRoomId;
        if (roomChanged || string.IsNullOrWhiteSpace(normalizedRoomId))
        {
            _state.State.IsTyping = false;
        }

        await _state.WriteStateAsync();
    }

    public Task<ConnectionSessionInfo> GetCurrent()
    {
        return Task.FromResult(new ConnectionSessionInfo
        {
            UserId = _state.State.UserId,
            RoomId = _state.State.RoomId,
            IsTyping = _state.State.IsTyping
        });
    }

    public async Task<bool> SetTypingState(bool isTyping)
    {
        if (_state.State.IsTyping == isTyping)
        {
            return false;
        }

        _state.State.IsTyping = isTyping;
        await _state.WriteStateAsync();
        return true;
    }

    public async Task Clear()
    {
        if (string.IsNullOrWhiteSpace(_state.State.UserId)
            && string.IsNullOrWhiteSpace(_state.State.RoomId)
            && !_state.State.IsTyping)
        {
            return;
        }

        _state.State.UserId = string.Empty;
        _state.State.RoomId = string.Empty;
        _state.State.IsTyping = false;
        await _state.WriteStateAsync();
    }
}
