import type { RefObject } from "react";
import type { ChatMessage } from "../types";

type MessageListProps = {
  messages: ChatMessage[];
  localUserId: string;
  readReceipts: Record<string, number>;
  quickEmojis: string[];
  chatBoxRef: RefObject<HTMLElement>;
  listEndRef: RefObject<HTMLDivElement>;
  onToggleReaction: (sequence: number, emoji: string) => void;
};

export default function MessageList(props: MessageListProps) {
  const readUsersForMessage = (message: ChatMessage) => {
    const readers: string[] = [];
    for (const [reader, seq] of Object.entries(props.readReceipts)) {
      if (reader === props.localUserId) {
        continue;
      }
      if (Number(seq || 0) >= Number(message.sequence || 0)) {
        readers.push(reader);
      }
    }
    return readers;
  };

  return (
    <section className="chat" ref={props.chatBoxRef}>
      {props.messages.map((m) => {
        const key = m.sequence > 0 ? "s:" + m.sequence : "c:" + (m.clientMessageId || m.sentAt);
        const isMine = m.userId === props.localUserId;
        const readers = isMine ? readUsersForMessage(m) : [];

        const renderedReactions = m.reactions.slice();
        for (const emoji of props.quickEmojis) {
          if (!renderedReactions.some((x) => x.emoji === emoji)) {
            renderedReactions.push({ emoji, count: 0, users: [] });
          }
        }

        return (
          <div className="msg" key={key}>
            <div className="meta">
              <span>{new Date(m.sentAt).toLocaleTimeString()} | {m.userId}</span>
              {isMine ? (
                <span className={"read-state" + (readers.length ? " read" : "")}>{readers.length ? "Read by " + readers.slice(0, 2).join(", ") + (readers.length > 2 ? " +" + (readers.length - 2) : "") : "Sent"}</span>
              ) : null}
            </div>
            <div>{m.message}</div>
            <div className="reactions">
              {renderedReactions.map((r) => {
                const users = Array.isArray(r.users) ? r.users : [];
                const active = users.includes(props.localUserId);
                return (
                  <button
                    className={"reaction-chip" + (active ? " active" : "")}
                    type="button"
                    key={(m.sequence || 0) + "-" + r.emoji}
                    onClick={() => props.onToggleReaction(Number(m.sequence || 0), String(r.emoji || ""))}
                  >
                    {r.emoji} {Number(r.count || 0)}
                  </button>
                );
              })}
            </div>
          </div>
        );
      })}
      <div ref={props.listEndRef}></div>
    </section>
  );
}
