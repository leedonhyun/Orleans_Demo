import type { RefObject } from "react";
import type { ChatMessage } from "../types";

type MessageListProps = {
  messages: ChatMessage[];
  participantCount: number;
  localUserId: string;
  readReceipts: Record<string, number>;
  quickEmojis: string[];
  chatBoxRef: RefObject<HTMLElement>;
  listEndRef: RefObject<HTMLDivElement>;
  onToggleReaction: (sequence: number, emoji: string) => void;
};

export default function MessageList(props: MessageListProps) {
  const readCountForMessage = (message: ChatMessage) => {
    let count = 0;
    for (const [reader, seq] of Object.entries(props.readReceipts)) {
      if (reader === message.userId) {
        continue;
      }
      if (Number(seq || 0) >= Number(message.sequence || 0)) {
        count += 1;
      }
    }
    return count;
  };

  const unreadRemainingForMessage = (message: ChatMessage) => {
    const others = Math.max(0, props.participantCount - 1);
    if (others === 0) {
      return 0;
    }

    if (Number(message.sequence || 0) <= 0) {
      return others;
    }

    const readCount = Math.min(others, readCountForMessage(message));
    return Math.max(0, others - readCount);
  };

  return (
    <section className="chat" ref={props.chatBoxRef}>
      {props.messages.map((m) => {
        const key = m.sequence > 0 ? "s:" + m.sequence : "c:" + (m.clientMessageId || m.sentAt);
        const unreadRemaining = unreadRemainingForMessage(m);

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
              {unreadRemaining > 0 ? (
                <span className="read-state read">{"읽지않음 " + unreadRemaining}</span>
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
