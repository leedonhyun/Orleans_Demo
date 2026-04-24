type ControlsProps = {
  roomId: string;
  userId: string;
  busyJoin: boolean;
  setRoomId: (value: string) => void;
  setUserId: (value: string) => void;
  onJoin: () => void;
  onLeave: () => void;
};

export default function Controls(props: ControlsProps) {
  return (
    <section className="controls">
      <input
        placeholder="roomId (예: lobby)"
        value={props.roomId}
        onChange={(e) => props.setRoomId(e.target.value)}
      />
      <input
        placeholder="userId (예: player-1)"
        value={props.userId}
        onChange={(e) => props.setUserId(e.target.value)}
      />
      <button onClick={props.onJoin} disabled={props.busyJoin}>Join</button>
      <button onClick={props.onLeave}>Leave</button>
    </section>
  );
}
