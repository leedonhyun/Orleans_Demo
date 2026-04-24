using System;
using System.Collections.Generic;
using System.Text;

namespace GrainImplement;

using GrainInterfaces;

using Orleans;
public class PlayerState
{
    public string CurrentRoomId { get; set; }
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

    public async Task JoinRoom(string roomId)
    {
        var playerId = this.GetPrimaryKeyString();

        // 1) 이미 같은 방이면 무시
        if (_state.State.CurrentRoomId == roomId)
            return;

        // 2) 이전 방에서 나가기
        if (!string.IsNullOrEmpty(_state.State.CurrentRoomId))
        {
            var oldRoom = GrainFactory.GetGrain<IRoomGrain>(_state.State.CurrentRoomId);
            await oldRoom.Leave(playerId);
        }

        // 3) 새 방에 입장
        var newRoom = GrainFactory.GetGrain<IRoomGrain>(roomId);
        await newRoom.Join(playerId);

        // 4) 상태 업데이트 + 저장
        _state.State.CurrentRoomId = roomId;
        await _state.WriteStateAsync();

        Console.WriteLine($"[Player:{playerId}] joined room {roomId}");
    }

    public async Task LeaveRoom()
    {
        var playerId = this.GetPrimaryKeyString();

        if (string.IsNullOrEmpty(_state.State.CurrentRoomId))
            return;

        var room = GrainFactory.GetGrain<IRoomGrain>(_state.State.CurrentRoomId);
        await room.Leave(playerId);

        _state.State.CurrentRoomId = null;
        await _state.WriteStateAsync();
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

