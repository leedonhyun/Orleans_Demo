import type { Status } from "../types";

type HeaderProps = {
  status: Status;
};

export default function Header({ status }: HeaderProps) {
  return (
    <section className="top">
      <h1 className="title">Orleans Real-time Chat</h1>
      <p className="subtitle">SignalR + Redis Backplane + Read Receipt + Reactions + Ack/Retry</p>
      <span className="pill">
        <span className={"dot" + (status.ok ? " ok" : "")}></span>
        <span>{status.text}</span>
      </span>
    </section>
  );
}
