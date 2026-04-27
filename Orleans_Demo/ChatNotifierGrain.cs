using GrainInterfaces;

using Microsoft.AspNetCore.SignalR;

using Orleans;

public class ChatNotifierGrain : Grain, IChatNotifierGrain
{
    private readonly IHubContext<ChatHub> _hubContext;

    public ChatNotifierGrain(IHubContext<ChatHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task NotifyParticipantChanged(string roomId, int participants)
    {
        return _hubContext.Clients.Group(roomId).SendAsync("ParticipantChanged", new { roomId, participants });
    }

    public Task NotifyChatMessageReceived(string roomId, ChatMessage message)
    {
        return _hubContext.Clients.Group(roomId).SendAsync("ChatMessageReceived", message);
    }

    public Task NotifyMessageAck(string roomId, string userId, string clientMessageId, long sequence)
    {
        return _hubContext.Clients.Group(roomId).SendAsync("MessageAck", new
        {
            roomId,
            userId,
            clientMessageId,
            sequence
        });
    }

    public Task NotifyReactionUpdated(string roomId, long sequence, IReadOnlyList<ChatReaction> reactions)
    {
        return _hubContext.Clients.Group(roomId).SendAsync("ReactionUpdated", new
        {
            roomId,
            sequence,
            reactions
        });
    }

    public Task NotifyTypingChanged(string roomId, string userId, bool isTyping)
    {
        return _hubContext.Clients.Group(roomId).SendAsync("TypingChanged", new
        {
            roomId,
            userId,
            isTyping
        });
    }

    public Task NotifyMessageRead(string roomId, string userId, long sequence)
    {
        return _hubContext.Clients.Group(roomId).SendAsync("MessageRead", new
        {
            roomId,
            userId,
            sequence
        });
    }
}
