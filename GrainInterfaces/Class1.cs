namespace GrainInterfaces;

using Orleans;

[GenerateSerializer]
public sealed class ChatMessage
{
    [Id(0)]
    public long Sequence { get; set; }

    [Id(1)]
    public string UserId { get; set; } = string.Empty;

    [Id(2)]
    public string Message { get; set; } = string.Empty;

    [Id(3)]
    public DateTimeOffset SentAt { get; set; }

    [Id(4)]
    public string ClientMessageId { get; set; } = string.Empty;

    [Id(5)]
    public List<ChatReaction> Reactions { get; set; } = new();
}

[GenerateSerializer]
public sealed class ChatReaction
{
    [Id(0)]
    public string Emoji { get; set; } = string.Empty;

    [Id(1)]
    public int Count { get; set; }

    [Id(2)]
    public List<string> Users { get; set; } = new();
}

public interface IPlayerGrain : IGrainWithStringKey
{
    Task JoinRoom(string roomId);
    Task LeaveRoom();
    Task SendInput(string input);
}

public interface IRoomGrain : IGrainWithStringKey
{
    Task Join(string playerId);
    Task Leave(string playerId);
    Task Broadcast(string fromPlayerId, string message);
    Task<ChatMessage> SendChat(string fromPlayerId, string message, string? clientMessageId);
    Task<IReadOnlyList<ChatMessage>> GetRecentMessages(int take);
    Task<int> GetParticipantCount();
    Task MarkRead(string userId, long sequence);
    Task<IReadOnlyDictionary<string, long>> GetReadReceipts();
    Task<IReadOnlyList<ChatReaction>> ToggleReaction(long sequence, string emoji, string userId);
}

[GenerateSerializer]
public sealed class UserSessionInfo
{
    [Id(0)]
    public string ConnectionId { get; set; } = string.Empty;

    [Id(1)]
    public string RoomId { get; set; } = string.Empty;
}

public interface IUserSessionGrain : IGrainWithStringKey
{
    Task<UserSessionInfo> Activate(string connectionId, string roomId);
    Task<UserSessionInfo> GetCurrent();
    Task DeactivateIfMatch(string connectionId);
}

