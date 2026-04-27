using System;

namespace GrainImplement;

using GrainInterfaces;

using Orleans;
public class PlayerState
{
    public string? CurrentRoomId { get; set; }
    public int Level { get; set; } = 1;
    public int Exp { get; set; } = 0;
}
public class PlayerGrain : Grain, IPlayerGrain
{
    private readonly IPersistentState<PlayerState> _state;

    public PlayerGrain(
        [PersistentState("player", "Default")] IPersistentState<PlayerState> state)
    {
        _state = state;
    }

    public async Task<PlayerRoomTransition> JoinRoom(string roomId)
    {
        var normalizedRoomId = roomId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedRoomId))
        {
            return new PlayerRoomTransition
            {
                Changed = false,
                CurrentRoomId = _state.State.CurrentRoomId ?? string.Empty
            };
        }

        var playerId = this.GetPrimaryKeyString();
        var currentRoomId = _state.State.CurrentRoomId;

        // 1) 이미 같은 방이면 무시
        if (string.Equals(currentRoomId, normalizedRoomId, StringComparison.Ordinal))
        {
            var room = GrainFactory.GetGrain<IRoomGrain>(normalizedRoomId);
            var participants = await room.GetParticipantCount();
            return new PlayerRoomTransition
            {
                Changed = false,
                CurrentRoomId = normalizedRoomId,
                CurrentRoomParticipants = participants
            };
        }

        var previousRoomParticipants = 0;

        // 2) 이전 방에서 나가기
        if (!string.IsNullOrEmpty(currentRoomId))
        {
            var oldRoom = GrainFactory.GetGrain<IRoomGrain>(currentRoomId);
            previousRoomParticipants = await oldRoom.Leave(playerId);
        }

        // 3) 새 방에 입장
        var newRoom = GrainFactory.GetGrain<IRoomGrain>(normalizedRoomId);
        var currentRoomParticipants = await newRoom.Join(playerId);

        // 4) 상태 업데이트 + 저장
        _state.State.CurrentRoomId = normalizedRoomId;
        await _state.WriteStateAsync();

        Console.WriteLine($"[Player:{playerId}] joined room {normalizedRoomId}");

        return new PlayerRoomTransition
        {
            Changed = true,
            PreviousRoomId = currentRoomId ?? string.Empty,
            PreviousRoomParticipants = previousRoomParticipants,
            CurrentRoomId = normalizedRoomId,
            CurrentRoomParticipants = currentRoomParticipants
        };
    }

    public async Task<PlayerRoomLeaveResult> LeaveRoom()
    {
        var playerId = this.GetPrimaryKeyString();

        if (string.IsNullOrEmpty(_state.State.CurrentRoomId))
        {
            return new PlayerRoomLeaveResult
            {
                Changed = false,
                RoomId = string.Empty,
                Participants = 0
            };
        }

        var roomId = _state.State.CurrentRoomId;
        var room = GrainFactory.GetGrain<IRoomGrain>(roomId);
        var participants = await room.Leave(playerId);

        _state.State.CurrentRoomId = string.Empty;
        await _state.WriteStateAsync();

        return new PlayerRoomLeaveResult
        {
            Changed = true,
            RoomId = roomId,
            Participants = participants
        };
    }

    public async Task SendInput(string input)
    {
        if (string.IsNullOrEmpty(_state.State.CurrentRoomId))
            return;

        var room = GrainFactory.GetGrain<IRoomGrain>(_state.State.CurrentRoomId);
        await room.Broadcast(this.GetPrimaryKeyString(), input);
    }

    public Task<PlayerState> GetState()
    {
        return Task.FromResult(_state.State);
    }
}


//public class PlayerGrain : Grain, IPlayerGrain
//{
//    private string _currentRoomId;

//    public async Task JoinRoom(string roomId)
//    {
//        if (_currentRoomId == roomId) return;

//        if (_currentRoomId != null)
//        {
//            var oldRoom = GrainFactory.GetGrain<IRoomGrain>(_currentRoomId);
//            await oldRoom.Leave(this.GetPrimaryKeyString());
//        }

//        var room = GrainFactory.GetGrain<IRoomGrain>(roomId);
//        await room.Join(this.GetPrimaryKeyString());

//        _currentRoomId = roomId;
//    }

//    public async Task LeaveRoom()
//    {
//        if (_currentRoomId == null) return;

//        var room = GrainFactory.GetGrain<IRoomGrain>(_currentRoomId);
//        await room.Leave(this.GetPrimaryKeyString());

//        _currentRoomId = null;
//    }

//    public async Task SendInput(string input)
//    {
//        if (_currentRoomId == null) return;

//        var room = GrainFactory.GetGrain<IRoomGrain>(_currentRoomId);
//        await room.Broadcast(this.GetPrimaryKeyString(), input);
//    }
//}

