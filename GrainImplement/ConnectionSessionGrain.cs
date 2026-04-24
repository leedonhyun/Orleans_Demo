namespace GrainImplement;

using GrainInterfaces;
using Orleans;

public class ConnectionSessionState
{
    public string UserId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
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
        _state.State.UserId = userId?.Trim() ?? string.Empty;
        _state.State.RoomId = roomId?.Trim() ?? string.Empty;
        await _state.WriteStateAsync();
    }

    public Task<ConnectionSessionInfo> GetCurrent()
    {
        return Task.FromResult(new ConnectionSessionInfo
        {
            UserId = _state.State.UserId,
            RoomId = _state.State.RoomId
        });
    }

    public async Task Clear()
    {
        if (string.IsNullOrWhiteSpace(_state.State.UserId) && string.IsNullOrWhiteSpace(_state.State.RoomId))
        {
            return;
        }

        _state.State.UserId = string.Empty;
        _state.State.RoomId = string.Empty;
        await _state.WriteStateAsync();
    }
}
