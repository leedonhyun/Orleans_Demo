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
    Task<PlayerRoomTransition> JoinRoom(string roomId);
    Task<PlayerRoomLeaveResult> LeaveRoom();
    Task SendInput(string input);
}

[GenerateSerializer]
public sealed class PlayerRoomTransition
{
    [Id(0)]
    public bool Changed { get; set; }

    [Id(1)]
    public string PreviousRoomId { get; set; } = string.Empty;

    [Id(2)]
    public int PreviousRoomParticipants { get; set; }

    [Id(3)]
    public string CurrentRoomId { get; set; } = string.Empty;

    [Id(4)]
    public int CurrentRoomParticipants { get; set; }
}

[GenerateSerializer]
public sealed class PlayerRoomLeaveResult
{
    [Id(0)]
    public bool Changed { get; set; }

    [Id(1)]
    public string RoomId { get; set; } = string.Empty;

    [Id(2)]
    public int Participants { get; set; }
}

public interface IRoomGrain : IGrainWithStringKey
{
    Task<int> Join(string playerId);
    Task<int> Leave(string playerId);
    Task Broadcast(string fromPlayerId, string message);
    Task<ChatMessage> SendChat(string fromPlayerId, string message, string? clientMessageId);
    Task<IReadOnlyList<ChatMessage>> GetRecentMessages(int take);
    Task<int> GetParticipantCount();
    Task<bool> MarkRead(string userId, long sequence);
    Task<IReadOnlyDictionary<string, long>> GetReadReceipts();
    Task<IReadOnlyList<ChatReaction>> ToggleReaction(long sequence, string emoji, string userId);
}

public interface IChatNotifierGrain : IGrainWithStringKey
{
    Task NotifyParticipantChanged(string roomId, int participants);
    Task NotifyChatMessageReceived(string roomId, ChatMessage message);
    Task NotifyMessageAck(string roomId, string userId, string clientMessageId, long sequence);
    Task NotifyReactionUpdated(string roomId, long sequence, IReadOnlyList<ChatReaction> reactions);
    Task NotifyTypingChanged(string roomId, string userId, bool isTyping);
    Task NotifyMessageRead(string roomId, string userId, long sequence);
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
    Task<UserSessionInfo> GetCurrent();
    Task BindConnection(string connectionId);
    Task<bool> SetRoomIfConnectionMatch(string connectionId, string roomId);
    Task<bool> ClearIfConnectionMatch(string connectionId);
}

[GenerateSerializer]
public sealed class ConnectionSessionInfo
{
    [Id(0)]
    public string UserId { get; set; } = string.Empty;

    [Id(1)]
    public string RoomId { get; set; } = string.Empty;

    [Id(2)]
    public bool IsTyping { get; set; }
}

public interface IConnectionSessionGrain : IGrainWithStringKey
{
    Task Upsert(string userId, string roomId);
    Task<ConnectionSessionInfo> GetCurrent();
    Task<bool> SetTypingState(bool isTyping);
    Task Clear();
}

