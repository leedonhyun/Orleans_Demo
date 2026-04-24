namespace GrainImplement;

using GrainInterfaces;

using Orleans;

public class RoomState
{
    public HashSet<string> Players { get; set; } = new();
    public List<ChatMessage> Messages { get; set; } = new();
    public Dictionary<string, long> ProcessedClientMessages { get; set; } = new();
    public Dictionary<string, long> ReadReceiptsByUser { get; set; } = new();
    public long NextSequence { get; set; }
    public string MapName { get; set; } = "default_map";
    public int MaxPlayers { get; set; } = 100;
}

public class RoomGrain : Grain, IRoomGrain
{
    private const int MaxMessageHistory = 500;
    private const int MaxProcessedMessageIds = 2_000;

    private readonly IPersistentState<RoomState> _state;

    public RoomGrain(
        [PersistentState("room", "Default")] IPersistentState<RoomState> state)
    {
        _state = state;
    }

    public async Task Join(string playerId)
    {
        if (_state.State.Players.Add(playerId))
        {
            await _state.WriteStateAsync();
            Console.WriteLine($"[Room:{this.GetPrimaryKeyString()}] {playerId} joined");
        }
    }

    public async Task Leave(string playerId)
    {
        var changed = false;

        if (_state.State.Players.Remove(playerId))
        {
            changed = true;
            Console.WriteLine($"[Room:{this.GetPrimaryKeyString()}] {playerId} left");
        }

        // Keep read receipts aligned with active participants.
        if (_state.State.ReadReceiptsByUser.Remove(playerId))
        {
            changed = true;
        }

        if (changed)
        {
            await _state.WriteStateAsync();
        }

    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        await _state.WriteStateAsync();
        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    public Task Broadcast(string fromPlayerId, string message)
    {
        return SendChat(fromPlayerId, message, null);
    }

    public async Task<ChatMessage> SendChat(string fromPlayerId, string message, string? clientMessageId)
    {
        var trimmedUserId = fromPlayerId?.Trim() ?? string.Empty;
        var trimmedMessage = message?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trimmedUserId) || string.IsNullOrWhiteSpace(trimmedMessage))
        {
            return new ChatMessage();
        }

        var dedupeKey = string.Empty;
        if (!string.IsNullOrWhiteSpace(clientMessageId))
        {
            dedupeKey = $"{trimmedUserId}:{clientMessageId}";
            if (_state.State.ProcessedClientMessages.TryGetValue(dedupeKey, out var existingSequence))
            {
                var existingMessage = _state.State.Messages.LastOrDefault(x => x.Sequence == existingSequence);
                if (existingMessage is not null)
                {
                    return CloneMessage(existingMessage);
                }

                return new ChatMessage
                {
                    Sequence = existingSequence,
                    UserId = trimmedUserId,
                    Message = trimmedMessage,
                    SentAt = DateTimeOffset.UtcNow,
                    ClientMessageId = clientMessageId
                };
            }
        }

        _state.State.NextSequence++;
        var chatMessage = new ChatMessage
        {
            Sequence = _state.State.NextSequence,
            UserId = trimmedUserId,
            Message = trimmedMessage,
            SentAt = DateTimeOffset.UtcNow,
            ClientMessageId = clientMessageId ?? string.Empty,
            Reactions = new List<ChatReaction>()
        };

        _state.State.Messages.Add(chatMessage);
        if (!string.IsNullOrWhiteSpace(dedupeKey))
        {
            _state.State.ProcessedClientMessages[dedupeKey] = chatMessage.Sequence;
            if (_state.State.ProcessedClientMessages.Count > MaxProcessedMessageIds)
            {
                var removeCount = _state.State.ProcessedClientMessages.Count - MaxProcessedMessageIds;
                foreach (var key in _state.State.ProcessedClientMessages.Keys.Take(removeCount).ToArray())
                {
                    _state.State.ProcessedClientMessages.Remove(key);
                }
            }
        }

        if (_state.State.Messages.Count > MaxMessageHistory)
        {
            _state.State.Messages.RemoveRange(0, _state.State.Messages.Count - MaxMessageHistory);
        }

        await _state.WriteStateAsync();

        Console.WriteLine($"[Room:{this.GetPrimaryKeyString()}] {trimmedUserId}: {trimmedMessage}");
        return CloneMessage(chatMessage);
    }

    public Task<IReadOnlyList<ChatMessage>> GetRecentMessages(int take)
    {
        if (take <= 0)
        {
            take = 1;
        }

        if (take > MaxMessageHistory)
        {
            take = MaxMessageHistory;
        }

        var count = _state.State.Messages.Count;
        var start = Math.Max(0, count - take);
        var messages = _state.State.Messages.Skip(start).Select(CloneMessage).ToArray();
        return Task.FromResult<IReadOnlyList<ChatMessage>>(messages);
    }

    public Task<int> GetParticipantCount()
    {
        return Task.FromResult(_state.State.Players.Count);
    }

    public async Task MarkRead(string userId, long sequence)
    {
        if (string.IsNullOrWhiteSpace(userId) || sequence <= 0)
        {
            return;
        }

        if (_state.State.ReadReceiptsByUser.TryGetValue(userId, out var existing) && existing >= sequence)
        {
            return;
        }

        _state.State.ReadReceiptsByUser[userId] = sequence;
        await _state.WriteStateAsync();
    }

    public Task<IReadOnlyDictionary<string, long>> GetReadReceipts()
    {
        var map = _state.State.ReadReceiptsByUser
            .Where(x => _state.State.Players.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Value);
        return Task.FromResult<IReadOnlyDictionary<string, long>>(map);
    }

    public async Task<IReadOnlyList<ChatReaction>> ToggleReaction(long sequence, string emoji, string userId)
    {
        var message = _state.State.Messages.LastOrDefault(x => x.Sequence == sequence);
        if (message is null)
        {
            return Array.Empty<ChatReaction>();
        }

        var normalizedEmoji = emoji?.Trim() ?? string.Empty;
        var normalizedUserId = userId?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedEmoji) || string.IsNullOrWhiteSpace(normalizedUserId))
        {
            return message.Reactions.Select(CloneReaction).ToArray();
        }

        var reaction = message.Reactions.FirstOrDefault(x => x.Emoji == normalizedEmoji);
        if (reaction is null)
        {
            reaction = new ChatReaction
            {
                Emoji = normalizedEmoji,
                Users = new List<string>()
            };
            message.Reactions.Add(reaction);
        }

        if (reaction.Users.Contains(normalizedUserId))
        {
            reaction.Users.Remove(normalizedUserId);
        }
        else
        {
            reaction.Users.Add(normalizedUserId);
        }

        reaction.Count = reaction.Users.Count;
        message.Reactions.RemoveAll(x => x.Users.Count == 0);

        await _state.WriteStateAsync();
        return message.Reactions.Select(CloneReaction).ToArray();
    }

    private static ChatMessage CloneMessage(ChatMessage input)
    {
        return new ChatMessage
        {
            Sequence = input.Sequence,
            UserId = input.UserId,
            Message = input.Message,
            SentAt = input.SentAt,
            ClientMessageId = input.ClientMessageId,
            Reactions = input.Reactions.Select(CloneReaction).ToList()
        };
    }

    private static ChatReaction CloneReaction(ChatReaction input)
    {
        return new ChatReaction
        {
            Emoji = input.Emoji,
            Count = input.Count,
            Users = input.Users.ToList()
        };
    }

    public Task<RoomState> GetState()
    {
        return Task.FromResult(_state.State);
    }
}

//public class RoomGrain : Grain, IRoomGrain
//{
//    private readonly ConcurrentDictionary<string, bool> _players = new();

//    public Task Join(string playerId)
//    {
//        _players[playerId] = true;
//        Console.WriteLine($"[Room:{this.GetPrimaryKeyString()}] {playerId} joined");
//        return Task.CompletedTask;
//    }

//    public Task Leave(string playerId)
//    {
//        _players.TryRemove(playerId, out _);
//        Console.WriteLine($"[Room:{this.GetPrimaryKeyString()}] {playerId} left");
//        return Task.CompletedTask;
//    }

//    public Task Broadcast(string fromPlayerId, string message)
//    {
//        // 실제로는 여기서 SignalR, WebSocket, Stream 등으로 클라이언트에 전달
//        Console.WriteLine($"[Room:{this.GetPrimaryKeyString()}] {fromPlayerId}: {message}");
//        return Task.CompletedTask;
//    }
//}

