import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { HubConnection, HubConnectionBuilder } from "@microsoft/signalr";
import Header from "./components/Header";
import Controls from "./components/Controls";
import MessageList from "./components/MessageList";
import Composer from "./components/Composer";
import type { BusyState, ChatMessage, Reaction, Status } from "./types";

const ACK_TIMEOUT_MS = 2000;
const MAX_SEND_RETRY = 3;
const QUICK_EMOJIS = ["👍", "❤️", "😂"];

type PendingAck = {
  roomId: string;
  userId: string;
  message: string;
  attempt: number;
  timerId: ReturnType<typeof setTimeout> | null;
};

function normalizeReaction(item: unknown): Reaction {
  const value = item as Record<string, unknown>;
  const users = value.users ?? value.Users;
  return {
    emoji: String(value.emoji ?? value.Emoji ?? ""),
    count: Number(value.count ?? value.Count ?? 0),
    users: Array.isArray(users) ? users.map((x) => String(x)) : []
  };
}

function normalizeMessage(item: unknown): ChatMessage {
  const value = item as Record<string, unknown>;
  const rawReactions = value.reactions ?? value.Reactions;
  return {
    sequence: Number(value.sequence ?? value.Sequence ?? 0),
    userId: String(value.userId ?? value.UserId ?? "unknown"),
    message: String(value.message ?? value.Message ?? ""),
    sentAt: String(value.sentAt ?? value.SentAt ?? new Date().toISOString()),
    clientMessageId: String(value.clientMessageId ?? value.ClientMessageId ?? ""),
    reactions: Array.isArray(rawReactions) ? rawReactions.map(normalizeReaction) : []
  };
}

function normalizeRoomSnapshot(data: unknown): { participants: number; messages: ChatMessage[]; readReceipts: Record<string, number>; } {
  const value = data as Record<string, unknown>;
  const participants = Number(value.participants ?? value.Participants ?? 0);
  const rawMessages = value.messages ?? value.Messages;
  const rawReadReceipts = value.readReceipts ?? value.ReadReceipts;

  const messages = Array.isArray(rawMessages) ? rawMessages.map(normalizeMessage) : [];
  const readReceipts: Record<string, number> = {};

  if (rawReadReceipts && typeof rawReadReceipts === "object") {
    for (const [key, val] of Object.entries(rawReadReceipts as Record<string, unknown>)) {
      readReceipts[key] = Number(val ?? 0);
    }
  }

  return { participants, messages, readReceipts };
}

function compareMessages(a: ChatMessage, b: ChatMessage) {
  const va = a.sequence > 0 ? a.sequence : Number.MAX_SAFE_INTEGER;
  const vb = b.sequence > 0 ? b.sequence : Number.MAX_SAFE_INTEGER;
  if (va !== vb) {
    return va - vb;
  }

  const ta = new Date(a.sentAt).getTime();
  const tb = new Date(b.sentAt).getTime();
  return ta - tb;
}

function upsertMessageList(list: ChatMessage[], message: ChatMessage): ChatMessage[] {
  const normalized = normalizeMessage(message);
  const next = list.slice();

  let existingIdx = next.findIndex((x) => x.sequence === normalized.sequence && normalized.sequence > 0);
  if (existingIdx < 0 && normalized.clientMessageId) {
    existingIdx = next.findIndex((x) => x.clientMessageId && x.clientMessageId === normalized.clientMessageId);
  }

  if (existingIdx >= 0) {
    next[existingIdx] = { ...next[existingIdx], ...normalized };
  } else {
    next.push(normalized);
  }

  next.sort(compareMessages);
  if (next.length > 200) {
    next.splice(0, next.length - 200);
  }

  return next;
}

function getMaxSequence(messages: ChatMessage[]) {
  let max = 0;
  for (const message of messages) {
    if (message.sequence > max) {
      max = message.sequence;
    }
  }
  return max;
}

async function callApi(url: string, method = "GET", body: unknown = null) {
  const options: RequestInit = {
    method,
    headers: {
      "Content-Type": "application/json"
    }
  };

  if (body !== null) {
    options.body = JSON.stringify(body);
  }

  const res = await fetch(url, options);
  if (!res.ok) {
    const txt = await res.text();
    const err = new Error(txt || "요청 실패") as Error & { status?: number };
    err.status = res.status;
    throw err;
  }

  if (res.status === 204) {
    return {};
  }

  const contentType = res.headers.get("content-type") || "";
  if (contentType.includes("application/json")) {
    return res.json();
  }

  const txt = await res.text();
  if (!txt) {
    return {};
  }

  try {
    return JSON.parse(txt);
  } catch {
    return { raw: txt };
  }
}

function generateClientMessageId() {
  if (window.crypto?.randomUUID) {
    return window.crypto.randomUUID();
  }
  return "msg-" + Date.now() + "-" + Math.floor(Math.random() * 1000000);
}

export default function App() {
  const [roomId, setRoomId] = useState("lobby");
  const [userId, setUserId] = useState("player-1");
  const [message, setMessage] = useState("");
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [readReceipts, setReadReceipts] = useState<Record<string, number>>({});
  const [typingUsers, setTypingUsers] = useState<string[]>([]);
  const [status, setStatus] = useState<Status>({ ok: false, text: "Disconnected" });
  const [isJoined, setIsJoined] = useState(false);
  const [connectedRoomId, setConnectedRoomId] = useState("");
  const [busy, setBusy] = useState<BusyState>({ join: false, send: false });

  const chatBoxRef = useRef<HTMLElement>(null);
  const pollTimerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const typingDebounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pendingAcksRef = useRef(new Map<string, PendingAck>());
  const hubConnectionRef = useRef<HubConnection | null>(null);
  const localUserIdRef = useRef("");
  const lastReadSentSequenceRef = useRef(0);
  const suppressPollRefreshUntilRef = useRef(0);
  const isTypingRef = useRef(false);

  const roomIdRef = useRef(roomId);
  const userIdRef = useRef(userId);
  const connectedRoomIdRef = useRef(connectedRoomId);
  const isJoinedRef = useRef(isJoined);

  useEffect(() => {
    roomIdRef.current = roomId;
  }, [roomId]);

  useEffect(() => {
    userIdRef.current = userId;
  }, [userId]);

  useEffect(() => {
    connectedRoomIdRef.current = connectedRoomId;
  }, [connectedRoomId]);

  useEffect(() => {
    isJoinedRef.current = isJoined;
  }, [isJoined]);

  const clearAckTimer = useCallback((clientMessageId: string) => {
    const pending = pendingAcksRef.current.get(clientMessageId);
    if (!pending) {
      return;
    }
    if (pending.timerId) {
      clearTimeout(pending.timerId);
    }
  }, []);

  const acknowledgeMessage = useCallback((clientMessageId: string) => {
    clearAckTimer(clientMessageId);
    pendingAcksRef.current.delete(clientMessageId);
  }, [clearAckTimer]);

  const setDisconnected = useCallback((text: string) => {
    setIsJoined(false);
    setConnectedRoomId("");
    localUserIdRef.current = "";
    lastReadSentSequenceRef.current = 0;
    setMessages([]);
    setReadReceipts({});
    setTypingUsers([]);

    for (const key of pendingAcksRef.current.keys()) {
      clearAckTimer(key);
    }
    pendingAcksRef.current.clear();

    if (pollTimerRef.current) {
      clearInterval(pollTimerRef.current);
      pollTimerRef.current = null;
    }

    setStatus({ ok: false, text });
  }, [clearAckTimer]);

  const notifyTyping = useCallback(async (nextTyping: boolean) => {
    const hub = hubConnectionRef.current;
    if (!isJoinedRef.current || !hub || hub.state !== "Connected" || !connectedRoomIdRef.current) {
      return;
    }
    if (isTypingRef.current === nextTyping) {
      return;
    }

    isTypingRef.current = nextTyping;
    const nextUserId = userIdRef.current.trim();
    if (!nextUserId) {
      return;
    }

    await hub.invoke("NotifyTyping", connectedRoomIdRef.current, nextUserId, nextTyping);
  }, []);

  const notifyRead = useCallback(async (sequence: number) => {
    const hub = hubConnectionRef.current;
    if (!isJoinedRef.current || !hub || hub.state !== "Connected" || !connectedRoomIdRef.current) {
      return;
    }
    if (sequence <= 0 || sequence <= lastReadSentSequenceRef.current) {
      return;
    }

    const nextUserId = userIdRef.current.trim();
    if (!nextUserId) {
      return;
    }

    lastReadSentSequenceRef.current = sequence;
    await hub.invoke("NotifyRead", connectedRoomIdRef.current, nextUserId, sequence);
  }, []);

  useEffect(() => {
    const box = chatBoxRef.current;
    if (!box) {
      return;
    }

    const nearBottom = (box.scrollHeight - box.scrollTop - box.clientHeight) < 24;
    if (nearBottom || messages.length <= 1) {
      box.scrollTop = box.scrollHeight;
    }

    const last = messages[messages.length - 1];
    if (last) {
      notifyRead(last.sequence).catch(console.error);
    }
  }, [messages, readReceipts, notifyRead]);

  useEffect(() => {
    const leaveOnUnload = () => {
      if (!isJoinedRef.current) {
        return;
      }

      const nextRoomId = roomIdRef.current.trim();
      const nextUserId = userIdRef.current.trim();
      if (!nextRoomId || !nextUserId) {
        return;
      }

      const url = "/api/chat/" + encodeURIComponent(nextRoomId) + "/leave-keepalive?userId=" + encodeURIComponent(nextUserId);
      try {
        if (navigator.sendBeacon) {
          navigator.sendBeacon(url, new Blob([], { type: "text/plain" }));
        } else {
          fetch(url, { method: "POST", keepalive: true });
        }
      } catch {
      }
    };

    window.addEventListener("pagehide", leaveOnUnload);
    window.addEventListener("beforeunload", leaveOnUnload);

    return () => {
      window.removeEventListener("pagehide", leaveOnUnload);
      window.removeEventListener("beforeunload", leaveOnUnload);
    };
  }, []);

  useEffect(() => {
    return () => {
      if (pollTimerRef.current) {
        clearInterval(pollTimerRef.current);
      }
      if (typingDebounceTimerRef.current) {
        clearTimeout(typingDebounceTimerRef.current);
      }

      for (const pending of pendingAcksRef.current.values()) {
        if (pending.timerId) {
          clearTimeout(pending.timerId);
        }
      }
      pendingAcksRef.current.clear();

      const hub = hubConnectionRef.current;
      if (hub && hub.state === "Connected") {
        void hub.stop();
      }
    };
  }, []);

  const ensureHubConnection = useCallback(async () => {
    if (!hubConnectionRef.current) {
      const hub = new HubConnectionBuilder()
        .withUrl("/hubs/chat")
        .withAutomaticReconnect()
        .build();

      hub.on("ChatMessageReceived", (incoming: unknown) => {
        const normalized = normalizeMessage(incoming);
        setMessages((prev) => upsertMessageList(prev, normalized));

        if (normalized.userId === localUserIdRef.current && normalized.clientMessageId) {
          acknowledgeMessage(normalized.clientMessageId);
        }
      });

      hub.on("MessageAck", (payload: unknown) => {
        const value = payload as Record<string, unknown>;
        const ackUserId = String(value.userId ?? value.UserId ?? "");
        const clientMessageId = String(value.clientMessageId ?? value.ClientMessageId ?? "");
        if (!clientMessageId || ackUserId !== localUserIdRef.current) {
          return;
        }
        acknowledgeMessage(clientMessageId);
      });

      hub.on("ParticipantChanged", (payload: unknown) => {
        const value = payload as Record<string, unknown>;
        const participants = Number(value.participants ?? value.Participants ?? 0);
        setStatus({ ok: true, text: "Connected | users: " + participants });
      });

      hub.on("TypingChanged", (payload: unknown) => {
        const value = payload as Record<string, unknown>;
        const typingUser = String(value.userId ?? value.UserId ?? "").trim();
        const nextTyping = Boolean(value.isTyping ?? value.IsTyping);

        if (!typingUser || typingUser === localUserIdRef.current) {
          return;
        }

        setTypingUsers((prev) => {
          const set = new Set(prev);
          if (nextTyping) {
            set.add(typingUser);
          } else {
            set.delete(typingUser);
          }
          return Array.from(set);
        });
      });

      hub.on("MessageRead", (payload: unknown) => {
        const value = payload as Record<string, unknown>;
        const readUserId = String(value.userId ?? value.UserId ?? "").trim();
        const sequence = Number(value.sequence ?? value.Sequence ?? 0);
        if (!readUserId || sequence <= 0) {
          return;
        }

        setReadReceipts((prev) => {
          const current = Number(prev[readUserId] || 0);
          if (sequence <= current) {
            return prev;
          }
          return { ...prev, [readUserId]: sequence };
        });
      });

      hub.on("ReactionUpdated", (payload: unknown) => {
        const value = payload as Record<string, unknown>;
        const sequence = Number(value.sequence ?? value.Sequence ?? 0);
        const reactionsRaw = value.reactions ?? value.Reactions;
        if (sequence <= 0) {
          return;
        }

        const reactions = Array.isArray(reactionsRaw) ? reactionsRaw.map(normalizeReaction) : [];
        setMessages((prev) => prev.map((m) => (m.sequence === sequence ? { ...m, reactions } : m)));
      });

      hub.on("ForceLogout", async (payload: unknown) => {
        const value = payload as Record<string, unknown>;
        const reason = String(value.reason ?? value.Reason ?? "다른 클라이언트에서 동일 userId로 접속했습니다.");
        setDisconnected("Disconnected (forced logout)");

        if (hub.state === "Connected") {
          try {
            await hub.stop();
          } catch {
          }
        }

        alert(reason);
      });

      hub.onclose(() => {
        setStatus({ ok: false, text: "Disconnected (SignalR closed)" });
      });

      hubConnectionRef.current = hub;
    }

    if (hubConnectionRef.current.state === "Disconnected") {
      await hubConnectionRef.current.start();
    }
  }, [acknowledgeMessage, setDisconnected]);

  const refresh = useCallback(async (options: { source?: string; force?: boolean } = {}) => {
    const source = String(options.source ?? "manual");
    const force = Boolean(options.force);
    const nextRoomId = roomIdRef.current.trim();
    if (!nextRoomId) {
      return;
    }

    if (source === "poll" && Date.now() < suppressPollRefreshUntilRef.current) {
      return;
    }

    const data = await callApi("/api/chat/" + encodeURIComponent(nextRoomId) + "/messages?take=100");
    const snapshot = normalizeRoomSnapshot(data);

    setMessages((prev) => {
      const snapshotMaxSeq = getMaxSequence(snapshot.messages);
      const localMaxSeq = getMaxSequence(prev);
      if (!force && source === "poll" && snapshotMaxSeq < localMaxSeq) {
        setStatus({ ok: true, text: "Connected | users: " + snapshot.participants });
        return prev;
      }
      return snapshot.messages.slice().sort(compareMessages);
    });

    setReadReceipts(snapshot.readReceipts);
    setStatus({ ok: true, text: "Connected | users: " + snapshot.participants });
  }, []);

  const scheduleRetry = useCallback((clientMessageId: string) => {
    const pending = pendingAcksRef.current.get(clientMessageId);
    if (!pending) {
      return;
    }

    pending.timerId = setTimeout(async () => {
      const latest = pendingAcksRef.current.get(clientMessageId);
      if (!latest) {
        return;
      }

      if (latest.attempt >= MAX_SEND_RETRY) {
        pendingAcksRef.current.delete(clientMessageId);
        setStatus({ ok: false, text: "Send failed after " + MAX_SEND_RETRY + " retries" });
        return;
      }

      latest.attempt += 1;
      try {
        await callApi("/api/chat/" + encodeURIComponent(latest.roomId) + "/message", "POST", {
          userId: latest.userId,
          message: latest.message,
          clientMessageId
        });
      } catch (err) {
        console.error(err);
      }

      scheduleRetry(clientMessageId);
    }, ACK_TIMEOUT_MS);
  }, []);

  const join = useCallback(async () => {
    const nextRoomId = roomId.trim();
    const nextUserId = userId.trim();
    if (!nextRoomId || !nextUserId) {
      return;
    }

    setBusy((prev) => ({ ...prev, join: true }));
    setStatus({ ok: true, text: "Connecting..." });
    try {
      localUserIdRef.current = nextUserId;
      lastReadSentSequenceRef.current = 0;

      await callApi("/api/chat/" + encodeURIComponent(nextRoomId) + "/join", "POST", { userId: nextUserId });
      await ensureHubConnection();

      const hub = hubConnectionRef.current;
      if (!hub) {
        return;
      }

      if (connectedRoomIdRef.current && connectedRoomIdRef.current !== nextRoomId) {
        await hub.invoke("LeaveRoom", connectedRoomIdRef.current);
      }

      await hub.invoke("JoinRoom", nextRoomId, nextUserId);
      setConnectedRoomId(nextRoomId);
      setIsJoined(true);
      setTypingUsers([]);

      await refresh({ source: "join" });

      if (pollTimerRef.current) {
        clearInterval(pollTimerRef.current);
      }

      pollTimerRef.current = setInterval(async () => {
        try {
          await refresh({ source: "poll" });
        } catch (err) {
          console.error(err);
          setStatus({ ok: false, text: "Disconnected (refresh failed)" });
        }
      }, 10000);
    } finally {
      setBusy((prev) => ({ ...prev, join: false }));
    }
  }, [ensureHubConnection, refresh, roomId, userId]);

  const leave = useCallback(async () => {
    const nextRoomId = roomId.trim();
    const nextUserId = userId.trim();
    if (!nextRoomId || !nextUserId) {
      return;
    }

    await callApi("/api/chat/" + encodeURIComponent(nextRoomId) + "/leave", "POST", { userId: nextUserId });
    await notifyTyping(false);

    const hub = hubConnectionRef.current;
    if (hub && hub.state === "Connected" && connectedRoomIdRef.current === nextRoomId) {
      await hub.invoke("LeaveRoom", nextRoomId);
    }

    setDisconnected("Disconnected");
  }, [notifyTyping, roomId, setDisconnected, userId]);

  const toggleReaction = useCallback(async (sequence: number, emoji: string) => {
    const nextRoomId = roomIdRef.current.trim();
    const nextUserId = userIdRef.current.trim();
    if (!nextRoomId || !nextUserId || sequence <= 0 || !emoji) {
      return;
    }

    try {
      await callApi("/api/chat/" + encodeURIComponent(nextRoomId) + "/react", "POST", {
        userId: nextUserId,
        sequence,
        emoji
      });
    } catch (err) {
      if ((err as { status?: number }).status === 404) {
        await callApi("/api/chat/" + encodeURIComponent(nextRoomId) + "/reaction", "POST", {
          userId: nextUserId,
          sequence,
          emoji
        });
        return;
      }

      throw err;
    }
  }, []);

  const send = useCallback(async () => {
    const nextRoomId = roomId.trim();
    const nextUserId = userId.trim();
    const nextMessage = message.trim();

    if (!nextRoomId || !nextUserId || !nextMessage) {
      return;
    }

    if (!isJoinedRef.current || connectedRoomIdRef.current !== nextRoomId) {
      setStatus({ ok: false, text: "Join 후 전송해 주세요." });
      return;
    }

    const clientMessageId = generateClientMessageId();
    setBusy((prev) => ({ ...prev, send: true }));
    await notifyTyping(false);
    setMessage("");

    setMessages((prev) =>
      upsertMessageList(prev, {
        sequence: 0,
        userId: nextUserId,
        message: nextMessage,
        sentAt: new Date().toISOString(),
        clientMessageId,
        reactions: []
      })
    );

    pendingAcksRef.current.set(clientMessageId, {
      roomId: nextRoomId,
      userId: nextUserId,
      message: nextMessage,
      attempt: 1,
      timerId: null
    });

    try {
      const sent = (await callApi("/api/chat/" + encodeURIComponent(nextRoomId) + "/message", "POST", {
        userId: nextUserId,
        message: nextMessage,
        clientMessageId
      })) as Record<string, unknown>;

      const sequence = Number(sent.sequence ?? sent.Sequence ?? 0);
      if (sequence <= 0) {
        await refresh({ source: "reconcile", force: true });
        suppressPollRefreshUntilRef.current = Date.now() + 1500;
      } else {
        setMessages((prev) =>
          upsertMessageList(prev, {
            sequence,
            userId: String(sent.userId ?? sent.UserId ?? nextUserId),
            message: String(sent.message ?? sent.Message ?? nextMessage),
            sentAt: String(sent.sentAt ?? sent.SentAt ?? new Date().toISOString()),
            clientMessageId: String(sent.clientMessageId ?? sent.ClientMessageId ?? clientMessageId),
            reactions: []
          })
        );
        suppressPollRefreshUntilRef.current = Date.now() + 1500;
      }

      setTimeout(() => {
        void refresh({ source: "reconcile", force: true });
      }, 250);
    } catch (err) {
      console.error(err);
      setStatus({ ok: false, text: "Send failed: " + (err as Error).message });
      await refresh({ source: "reconcile", force: true }).catch(console.error);
    } finally {
      scheduleRetry(clientMessageId);
      setBusy((prev) => ({ ...prev, send: false }));
    }
  }, [message, notifyTyping, refresh, roomId, scheduleRetry, userId]);

  const onMessageChange = useCallback(
    (nextValue: string) => {
      setMessage(nextValue);

      if (!isJoinedRef.current) {
        return;
      }

      const trimmed = nextValue.trim();

      if (typingDebounceTimerRef.current) {
        clearTimeout(typingDebounceTimerRef.current);
      }

      if (!trimmed) {
        void notifyTyping(false);
        return;
      }

      void notifyTyping(true);
      typingDebounceTimerRef.current = setTimeout(() => {
        void notifyTyping(false);
      }, 1200);
    },
    [notifyTyping]
  );

  const typingText = useMemo(() => {
    if (typingUsers.length === 0) {
      return "";
    }
    if (typingUsers.length === 1) {
      return typingUsers[0] + " 입력 중...";
    }
    if (typingUsers.length === 2) {
      return typingUsers[0] + ", " + typingUsers[1] + " 입력 중...";
    }
    return typingUsers.slice(0, 2).join(", ") + " 외 " + (typingUsers.length - 2) + "명 입력 중...";
  }, [typingUsers]);

  return (
    <main className="shell">
      <Header status={status} />
      <Controls
        roomId={roomId}
        userId={userId}
        busyJoin={busy.join}
        setRoomId={setRoomId}
        setUserId={setUserId}
        onJoin={() => {
          void join().catch((err: Error) => {
            setStatus({ ok: false, text: "Join failed: " + err.message });
            alert(err.message);
          });
        }}
        onLeave={() => {
          void leave().catch((err: Error) => {
            setStatus({ ok: false, text: "Leave failed: " + err.message });
            alert(err.message);
          });
        }}
      />
      <MessageList
        messages={messages}
        localUserId={localUserIdRef.current}
        readReceipts={readReceipts}
        quickEmojis={QUICK_EMOJIS}
        chatBoxRef={chatBoxRef}
        onToggleReaction={(sequence, emoji) => {
          void toggleReaction(sequence, emoji).catch(console.error);
        }}
      />
      <Composer
        message={message}
        typingText={typingText}
        busySend={busy.send}
        setMessageValue={onMessageChange}
        onSend={() => {
          void send().catch((err: Error) => {
            setStatus({ ok: false, text: "Send failed: " + err.message });
            alert(err.message);
          });
        }}
        onRefresh={() => {
          void refresh({ source: "manual" }).catch((err: Error) => {
            setStatus({ ok: false, text: "Refresh failed: " + err.message });
            alert(err.message);
          });
        }}
      />
    </main>
  );
}
