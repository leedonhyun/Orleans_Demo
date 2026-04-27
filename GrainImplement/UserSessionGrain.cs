namespace GrainImplement;

using GrainInterfaces;

using Orleans;

public class UserSessionGrain : Grain, IUserSessionGrain
{
    private string _connectionId = string.Empty;
    private string _roomId = string.Empty;

    public Task<UserSessionInfo> GetCurrent()
    {
        return Task.FromResult(new UserSessionInfo
        {
            ConnectionId = _connectionId,
            RoomId = _roomId
        });
    }

    public Task BindConnection(string connectionId)
    {
        _connectionId = connectionId?.Trim() ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task<bool> SetRoomIfConnectionMatch(string connectionId, string roomId)
    {
        var normalizedConnectionId = connectionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedConnectionId) || !string.Equals(_connectionId, normalizedConnectionId, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        _roomId = roomId?.Trim() ?? string.Empty;
        return Task.FromResult(true);
    }

    public Task<bool> ClearIfConnectionMatch(string connectionId)
    {
        var normalizedConnectionId = connectionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedConnectionId) || !string.Equals(_connectionId, normalizedConnectionId, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        _connectionId = string.Empty;
        _roomId = string.Empty;

        return Task.FromResult(true);
    }
}
