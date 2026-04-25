import type { RefObject } from "react";
import type { ChatMessage } from "../types";

const FILE_MESSAGE_PREFIX = "__file__:";

type FilePayload = {
  url: string;
  name: string;
  contentType: string;
  size: number;
};

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
  const parseFilePayload = (text: string): FilePayload | null => {
    if (!text || !text.startsWith(FILE_MESSAGE_PREFIX)) {
      return null;
    }

    try {
      const raw = JSON.parse(text.slice(FILE_MESSAGE_PREFIX.length)) as Record<string, unknown>;
      const url = String(raw.url ?? "");
      const name = String(raw.name ?? "");
      if (!url || !name) {
        return null;
      }

      return {
        url,
        name,
        contentType: String(raw.contentType ?? "application/octet-stream"),
        size: Number(raw.size ?? 0)
      };
    } catch {
      return null;
    }
  };

  const formatFileSize = (size: number) => {
    if (!Number.isFinite(size) || size <= 0) {
      return "";
    }

    if (size < 1024) {
      return size + " B";
    }
    if (size < 1024 * 1024) {
      return (size / 1024).toFixed(1) + " KB";
    }
    return (size / (1024 * 1024)).toFixed(1) + " MB";
  };

  const isImagePayload = (file: FilePayload) => {
    if (file.contentType.startsWith("image/")) {
      return true;
    }

    const lower = file.name.toLowerCase();
    return lower.endsWith(".png") || lower.endsWith(".jpg") || lower.endsWith(".jpeg") || lower.endsWith(".gif") || lower.endsWith(".webp") || lower.endsWith(".bmp");
  };

  const fileExtensionLabel = (fileName: string) => {
    const idx = fileName.lastIndexOf(".");
    if (idx < 0 || idx === fileName.length - 1) {
      return "FILE";
    }

    const ext = fileName.slice(idx + 1).toUpperCase();
    return ext.length > 5 ? ext.slice(0, 5) : ext;
  };

  const downloadFile = (file: FilePayload) => {
    const link = document.createElement("a");
    link.href = file.url;
    link.download = file.name || "download";
    link.rel = "noopener noreferrer";
    document.body.appendChild(link);
    link.click();
    link.remove();
  };

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
        const filePayload = parseFilePayload(m.message);

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
            {filePayload ? (
              <div className="file-message">
                <div className="file-row">
                  <div className="file-thumb" aria-hidden="true">
                    {isImagePayload(filePayload) ? (
                      <img src={filePayload.url} alt="" className="file-thumb-image" />
                    ) : (
                      <span className="file-thumb-ext">{fileExtensionLabel(filePayload.name)}</span>
                    )}
                  </div>
                  <div className="file-info">
                    <button
                      type="button"
                      className="file-link-button"
                      onClick={() => downloadFile(filePayload)}
                    >
                      {filePayload.name}
                    </button>
                    <div className="file-meta">{filePayload.contentType}{formatFileSize(filePayload.size) ? " | " + formatFileSize(filePayload.size) : ""}</div>
                  </div>
                </div>
              </div>
            ) : (
              <div>{m.message}</div>
            )}
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
