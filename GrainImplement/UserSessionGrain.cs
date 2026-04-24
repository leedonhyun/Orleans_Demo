namespace GrainImplement;

using GrainInterfaces;

using Orleans;

public class UserSessionGrain : Grain, IUserSessionGrain
{
    private string _connectionId = string.Empty;
    private string _roomId = string.Empty;

    public Task<UserSessionInfo> Activate(string connectionId, string roomId)
    {
        var previous = new UserSessionInfo
        {
            ConnectionId = _connectionId,
            RoomId = _roomId
        };

        _connectionId = connectionId?.Trim() ?? string.Empty;
        _roomId = roomId?.Trim() ?? string.Empty;

        return Task.FromResult(previous);
    }

    public Task<UserSessionInfo> GetCurrent()
    {
        return Task.FromResult(new UserSessionInfo
        {
            ConnectionId = _connectionId,
            RoomId = _roomId
        });
    }

    public Task DeactivateIfMatch(string connectionId)
    {
        if (!string.IsNullOrWhiteSpace(connectionId) && string.Equals(_connectionId, connectionId, StringComparison.Ordinal))
        {
            _connectionId = string.Empty;
            _roomId = string.Empty;
        }

        return Task.CompletedTask;
    }
}
