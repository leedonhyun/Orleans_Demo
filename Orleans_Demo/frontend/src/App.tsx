import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { HubConnection, HubConnectionBuilder } from "@microsoft/signalr";
import Header from "./components/Header";
import Controls from "./components/Controls";
import MessageList from "./components/MessageList";
import Composer from "./components/Composer";
import type { BusyState, ChatMessage, Reaction, Status } from "./types";

const ACK_TIMEOUT_MS = 2000;
const MAX_SEND_RETRY = 3;
const JOIN_STEP_TIMEOUT_MS = 12000;
const QUICK_EMOJIS = ["👍", "❤️", "😂"];
const MAX_UPLOAD_BYTES = 15 * 1024 * 1024;

type PendingAck = {
  roomId: string;
  userId: string;
  message: string;
  attempt: number;
  timerId: ReturnType<typeof setTimeout> | null;
};

type LobbyRoom = {
  roomId: string;
  name: string;
};

type LobbyUser = {
  userId: string;
  displayName: string;
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

function normalizeLobbyRoom(item: unknown): LobbyRoom {
  const value = item as Record<string, unknown>;
  const roomId = String(value.roomId ?? value.RoomId ?? "").trim();
  const name = String(value.name ?? value.Name ?? roomId).trim();
  return {
    roomId,
    name: name || roomId
  };
}

function normalizeLobbyUser(item: unknown): LobbyUser {
  const value = item as Record<string, unknown>;
  const userId = String(value.userId ?? value.UserId ?? "").trim();
  const displayName = String(value.displayName ?? value.DisplayName ?? userId).trim();
  return {
    userId,
    displayName: displayName || userId
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
  const ta = new Date(a.sentAt).getTime();
  const tb = new Date(b.sentAt).getTime();
  if (ta !== tb) {
    return ta - tb;
  }

  const va = a.sequence > 0 ? a.sequence : Number.MAX_SAFE_INTEGER;
  const vb = b.sequence > 0 ? b.sequence : Number.MAX_SAFE_INTEGER;
  return va - vb;
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
    credentials: "include",
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

async function withTimeout<T>(promise: Promise<T>, ms: number, label: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | null = null;
  try {
    return await Promise.race([
      promise,
      new Promise<T>((_, reject) => {
        timer = setTimeout(() => {
          reject(new Error(label + " timed out"));
        }, ms);
      })
    ]);
  } finally {
    if (timer) {
      clearTimeout(timer);
    }
  }
}

export default function App() {
  const [roomId, setRoomId] = useState("lobby");
  const [userId, setUserId] = useState("player-1");
  const [loginUserId, setLoginUserId] = useState("player-1");
  const [password, setPassword] = useState("");
  const [registerUserId, setRegisterUserId] = useState("");
  const [registerPassword, setRegisterPassword] = useState("");
  const [registerDisplayName, setRegisterDisplayName] = useState("");
  const [authScreen, setAuthScreen] = useState<"login" | "register">("login");
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [rooms, setRooms] = useState<LobbyRoom[]>([]);
  const [selectedRoomId, setSelectedRoomId] = useState("");
  const [roomUsers, setRoomUsers] = useState<LobbyUser[]>([]);
  const [message, setMessage] = useState("");
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [readReceipts, setReadReceipts] = useState<Record<string, number>>({});
  const [participantCount, setParticipantCount] = useState(0);
  const [typingUsers, setTypingUsers] = useState<string[]>([]);
  const [status, setStatus] = useState<Status>({ ok: false, text: "Disconnected" });
  const [isJoined, setIsJoined] = useState(false);
  const [connectedRoomId, setConnectedRoomId] = useState("");
  const [busy, setBusy] = useState<BusyState>({ join: false, send: false });
  const [busyLobby, setBusyLobby] = useState({ login: false, register: false, rooms: false, users: false });

  const chatBoxRef = useRef<HTMLElement>(null);
  const listEndRef = useRef<HTMLDivElement>(null);
  const pollTimerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const typingDebounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pendingAcksRef = useRef(new Map<string, PendingAck>());
  const hubConnectionRef = useRef<HubConnection | null>(null);
  const localUserIdRef = useRef("");
  const lastReadSentSequenceRef = useRef(0);
  const suppressPollRefreshUntilRef = useRef(0);
  const isTypingRef = useRef(false);
  const forceScrollOnNextRenderRef = useRef(false);

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
    setParticipantCount(0);
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

    // Always keep newest message visible at the bottom.
    listEndRef.current?.scrollIntoView({ block: "end" });
    box.scrollTop = box.scrollHeight;
    forceScrollOnNextRenderRef.current = false;

    const last = messages[messages.length - 1];
    if (last && document.visibilityState === "visible" && document.hasFocus()) {
      notifyRead(last.sequence).catch(console.error);
    }
  }, [messages, readReceipts, notifyRead]);

  useEffect(() => {
    const flushReadIfVisible = () => {
      if (document.visibilityState !== "visible" || !document.hasFocus()) {
        return;
      }

      const last = messages[messages.length - 1];
      if (last) {
        notifyRead(last.sequence).catch(console.error);
      }
    };

    document.addEventListener("visibilitychange", flushReadIfVisible);
    window.addEventListener("focus", flushReadIfVisible);

    return () => {
      document.removeEventListener("visibilitychange", flushReadIfVisible);
      window.removeEventListener("focus", flushReadIfVisible);
    };
  }, [messages, notifyRead]);

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
        setParticipantCount(participants);
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

      hub.onreconnecting(() => {
        setStatus({ ok: false, text: "Reconnecting..." });
      });

      hub.onreconnected(async () => {
        const rejoinRoomId = connectedRoomIdRef.current.trim();
        const rejoinUserId = userIdRef.current.trim();
        if (!isJoinedRef.current || !rejoinRoomId || !rejoinUserId) {
          setStatus({ ok: true, text: "Reconnected" });
          return;
        }

        try {
          await hub.invoke("JoinRoom", rejoinRoomId, rejoinUserId);
          setStatus({ ok: true, text: "Reconnected | room rejoined" });
        } catch (err) {
          console.error(err);
          setStatus({ ok: false, text: "Reconnected, but failed to rejoin room" });
        }
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
      const snapshotSorted = snapshot.messages.slice().sort(compareMessages);
      return snapshotSorted;
    });

    setReadReceipts(snapshot.readReceipts);
    setParticipantCount(snapshot.participants);
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
        const statusCode = (err as { status?: number }).status;
        if (statusCode === 401 || statusCode === 403) {
          pendingAcksRef.current.delete(clientMessageId);
          setStatus({ ok: false, text: statusCode === 401 ? "인증 세션이 만료되었습니다." : "로그인 계정과 userId가 달라 전송 재시도를 중단합니다." });
          return;
        }
        console.error(err);
      }

      scheduleRetry(clientMessageId);
    }, ACK_TIMEOUT_MS);
  }, []);

  const loadRooms = useCallback(async () => {
    setBusyLobby((prev) => ({ ...prev, rooms: true }));
    try {
      const data = await callApi("/api/lobby/rooms");
      const nextRooms = Array.isArray(data) ? data.map(normalizeLobbyRoom).filter((x) => x.roomId) : [];
      setRooms(nextRooms);

      if (nextRooms.length === 0) {
        setSelectedRoomId("");
        setRoomUsers([]);
        return;
      }

      const current = selectedRoomId.trim();
      const nextSelected = current && nextRooms.some((x) => x.roomId === current)
        ? current
        : nextRooms[0].roomId;

      setSelectedRoomId(nextSelected);
      setRoomId(nextSelected);
    } finally {
      setBusyLobby((prev) => ({ ...prev, rooms: false }));
    }
  }, [selectedRoomId]);

  const loadRoomUsers = useCallback(async (nextRoomId: string) => {
    const normalized = nextRoomId.trim();
    if (!normalized) {
      setRoomUsers([]);
      return;
    }

    setBusyLobby((prev) => ({ ...prev, users: true }));
    try {
      const data = await callApi("/api/lobby/rooms/" + encodeURIComponent(normalized) + "/users");
      const users = Array.isArray(data) ? data.map(normalizeLobbyUser).filter((x) => x.userId) : [];
      setRoomUsers(users);
    } finally {
      setBusyLobby((prev) => ({ ...prev, users: false }));
    }
  }, []);

  const login = useCallback(async () => {
    const nextUserId = loginUserId.trim();
    const nextPassword = password;
    if (!nextUserId || !nextPassword) {
      setStatus({ ok: false, text: "아이디/비밀번호를 입력해 주세요." });
      return;
    }

    setBusyLobby((prev) => ({ ...prev, login: true }));
    try {
      const response = (await callApi("/api/auth/login", "POST", {
        userId: nextUserId,
        password: nextPassword
      })) as Record<string, unknown>;

      const resolvedUserId = String(response.userId ?? response.UserId ?? nextUserId).trim() || nextUserId;
      setIsAuthenticated(true);
      setUserId(resolvedUserId);
      setLoginUserId(resolvedUserId);
      setAuthScreen("login");
      setStatus({ ok: true, text: "로그인 성공" });

      await loadRooms();
    } finally {
      setBusyLobby((prev) => ({ ...prev, login: false }));
    }
  }, [loginUserId, password, loadRooms]);

  const registerUser = useCallback(async () => {
    const nextUserId = registerUserId.trim();
    const nextPassword = registerPassword;
    const nextDisplayName = registerDisplayName.trim();

    if (!nextUserId || !nextPassword) {
      setStatus({ ok: false, text: "회원가입: 아이디/비밀번호를 입력해 주세요." });
      return;
    }

    setBusyLobby((prev) => ({ ...prev, register: true }));
    try {
      const response = (await callApi("/api/auth/register", "POST", {
        userId: nextUserId,
        password: nextPassword,
        displayName: nextDisplayName || null
      })) as Record<string, unknown>;

      const createdName = String(response.displayName ?? response.DisplayName ?? nextUserId).trim() || nextUserId;
      setRegisterDisplayName(createdName);
      setLoginUserId(nextUserId);
      setPassword("");
      setRegisterUserId("");
      setRegisterPassword("");
      setAuthScreen("login");
      setStatus({ ok: true, text: "회원가입 완료: 로그인해 주세요." });
    } finally {
      setBusyLobby((prev) => ({ ...prev, register: false }));
    }
  }, [registerUserId, registerPassword, registerDisplayName]);

  const selectRoom = useCallback(async (nextRoomId: string) => {
    const normalized = nextRoomId.trim();
    if (!normalized) {
      return;
    }

    setSelectedRoomId(normalized);
    setRoomId(normalized);
    await loadRoomUsers(normalized);
  }, [loadRoomUsers]);

  useEffect(() => {
    if (!isAuthenticated) {
      return;
    }

    if (!selectedRoomId) {
      setRoomUsers([]);
      return;
    }

    void loadRoomUsers(selectedRoomId).catch(console.error);
  }, [isAuthenticated, loadRoomUsers, selectedRoomId]);

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

      await withTimeout(ensureHubConnection(), JOIN_STEP_TIMEOUT_MS, "SignalR connect");

      const hub = hubConnectionRef.current;
      if (!hub) {
        throw new Error("SignalR hub is not initialized");
      }

      if (connectedRoomIdRef.current && connectedRoomIdRef.current !== nextRoomId) {
        await withTimeout(
          hub.invoke("LeaveRoom", connectedRoomIdRef.current),
          JOIN_STEP_TIMEOUT_MS,
          "Leave previous room"
        );
      }

      await withTimeout(
        hub.invoke("JoinRoom", nextRoomId, nextUserId),
        JOIN_STEP_TIMEOUT_MS,
        "Join room"
      );
      setConnectedRoomId(nextRoomId);
      setIsJoined(true);
      connectedRoomIdRef.current = nextRoomId;
      isJoinedRef.current = true;
      setTypingUsers([]);

      await withTimeout(refresh({ source: "join" }), JOIN_STEP_TIMEOUT_MS, "Initial refresh");

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

    await notifyTyping(false);

    const hub = hubConnectionRef.current;
    let leftByHub = false;
    if (hub && hub.state === "Connected" && connectedRoomIdRef.current === nextRoomId) {
      try {
        await hub.invoke("LeaveRoom", nextRoomId);
        leftByHub = true;
      } catch (err) {
        console.error(err);
      }
    }

    // Fallback for disconnected/unavailable hub sessions.
    if (!leftByHub) {
      await callApi("/api/chat/" + encodeURIComponent(nextRoomId) + "/leave", "POST", { userId: nextUserId });
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

    if (!isAuthenticated) {
      setStatus({ ok: false, text: "인증 세션이 없습니다. 로그인 후 다시 시도해 주세요." });
      return;
    }

    if (!nextRoomId || !nextUserId) {
      setStatus({ ok: false, text: "roomId/userId를 먼저 입력해 주세요." });
      return;
    }

    if (!nextMessage) {
      setStatus({ ok: false, text: "메시지를 입력해 주세요." });
      return;
    }

    if (!isJoinedRef.current || connectedRoomIdRef.current !== nextRoomId) {
      // Join state can be briefly stale after refresh/reconnect.
      // Do not block send: backend /message joins player to room and returns sequence.
      setStatus({ ok: false, text: "Join 상태 재동기화 중입니다. 전송을 시도합니다..." });
    }

    if (!localUserIdRef.current) {
      localUserIdRef.current = nextUserId;
    }

    // Ensure the sender immediately sees the newly added message.
    forceScrollOnNextRenderRef.current = true;

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

      // Immediately reconcile with server snapshot so the list reflects canonical order/state.
      await refresh({ source: "reconcile", force: true });
    } catch (err) {
      console.error(err);
      const statusCode = (err as { status?: number }).status;
      if (statusCode === 401) {
        pendingAcksRef.current.delete(clientMessageId);
        setDisconnected("인증 세션이 만료되었습니다. 다시 로그인해 주세요.");
        setIsAuthenticated(false);
        setAuthScreen("login");
        setStatus({ ok: false, text: "인증 세션이 만료되었습니다. 로그인 창으로 이동합니다." });
        return;
      }

      if (statusCode === 403) {
        pendingAcksRef.current.delete(clientMessageId);
        setStatus({ ok: false, text: "로그인 계정과 현재 userId가 다릅니다. userId를 로그인 아이디로 맞춰주세요." });
        return;
      }

      setStatus({ ok: false, text: "Send failed: " + (err as Error).message });
      await refresh({ source: "reconcile", force: true }).catch(console.error);
    } finally {
      scheduleRetry(clientMessageId);
      setBusy((prev) => ({ ...prev, send: false }));
    }
  }, [isAuthenticated, message, notifyTyping, refresh, roomId, scheduleRetry, setDisconnected, userId]);

  const sendFile = useCallback(async (file: File) => {
    const nextRoomId = roomId.trim();
    const nextUserId = userId.trim();

    if (!isAuthenticated) {
      setStatus({ ok: false, text: "인증 세션이 없습니다. 로그인 후 다시 시도해 주세요." });
      return;
    }

    if (!nextRoomId || !nextUserId) {
      setStatus({ ok: false, text: "roomId/userId를 먼저 입력해 주세요." });
      return;
    }

    if (!file) {
      return;
    }

    if (file.size > MAX_UPLOAD_BYTES) {
      setStatus({ ok: false, text: "파일은 15MB 이하만 전송할 수 있습니다." });
      return;
    }

    if (!isJoinedRef.current || connectedRoomIdRef.current !== nextRoomId) {
      setStatus({ ok: false, text: "Join 상태 재동기화 중입니다. 파일 전송을 시도합니다..." });
    }

    if (!localUserIdRef.current) {
      localUserIdRef.current = nextUserId;
    }

    forceScrollOnNextRenderRef.current = true;
    const clientMessageId = generateClientMessageId();

    setBusy((prev) => ({ ...prev, send: true }));
    await notifyTyping(false);

    try {
      const formData = new FormData();
      formData.append("userId", nextUserId);
      formData.append("clientMessageId", clientMessageId);
      formData.append("file", file);

      const res = await fetch("/api/chat/" + encodeURIComponent(nextRoomId) + "/file", {
        method: "POST",
        credentials: "include",
        body: formData
      });

      if (!res.ok) {
        const txt = await res.text();
        const err = new Error(txt || "파일 전송 실패") as Error & { status?: number };
        err.status = res.status;
        throw err;
      }

      const sent = (await res.json()) as Record<string, unknown>;
      const sequence = Number(sent.sequence ?? sent.Sequence ?? 0);

      if (sequence > 0) {
        setMessages((prev) =>
          upsertMessageList(prev, {
            sequence,
            userId: String(sent.userId ?? sent.UserId ?? nextUserId),
            message: String(sent.message ?? sent.Message ?? ""),
            sentAt: String(sent.sentAt ?? sent.SentAt ?? new Date().toISOString()),
            clientMessageId: String(sent.clientMessageId ?? sent.ClientMessageId ?? clientMessageId),
            reactions: []
          })
        );
      }

      suppressPollRefreshUntilRef.current = Date.now() + 1500;
      await refresh({ source: "reconcile", force: true });
    } catch (err) {
      console.error(err);
      const statusCode = (err as { status?: number }).status;
      if (statusCode === 401) {
        setDisconnected("인증 세션이 만료되었습니다. 다시 로그인해 주세요.");
        setIsAuthenticated(false);
        setAuthScreen("login");
        setStatus({ ok: false, text: "인증 세션이 만료되었습니다. 로그인 창으로 이동합니다." });
        return;
      }

      if (statusCode === 403) {
        setStatus({ ok: false, text: "로그인 계정과 현재 userId가 다릅니다. userId를 로그인 아이디로 맞춰주세요." });
        return;
      }

      setStatus({ ok: false, text: "File send failed: " + (err as Error).message });
      await refresh({ source: "reconcile", force: true }).catch(console.error);
    } finally {
      setBusy((prev) => ({ ...prev, send: false }));
    }
  }, [isAuthenticated, notifyTyping, refresh, roomId, setDisconnected, userId]);

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
    <main className={"shell" + (!isAuthenticated ? " auth-shell" : "")}>
      <Header status={status} />
      {!isAuthenticated ? (
        <section className="auth-only">
          <div className="auth-card">
            {authScreen === "login" ? (
              <>
                <h2 className="card-title">로그인</h2>
                <div className="auth-grid">
                  <input
                    placeholder="아이디"
                    value={loginUserId}
                    onChange={(e) => setLoginUserId(e.target.value)}
                    disabled={busyLobby.login || busyLobby.register}
                  />
                  <input
                    type="password"
                    placeholder="비밀번호"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    disabled={busyLobby.login || busyLobby.register}
                  />
                  <div className="auth-actions single">
                    <button
                      onClick={() => {
                        void login().catch((err: Error) => {
                          setStatus({ ok: false, text: "로그인 실패: " + err.message });
                          alert(err.message);
                        });
                      }}
                      disabled={busyLobby.login || busyLobby.register}
                    >
                      {busyLobby.login ? "로그인 중..." : "로그인"}
                    </button>
                  </div>
                </div>
                <div className="auth-switch">
                  <button
                    onClick={() => setAuthScreen("register")}
                    disabled={busyLobby.login || busyLobby.register}
                  >
                    회원가입
                  </button>
                </div>
              </>
            ) : (
              <>
                <h2 className="card-title">회원가입</h2>
                <div className="auth-grid">
                  <input
                    placeholder="회원가입 아이디"
                    value={registerUserId}
                    onChange={(e) => setRegisterUserId(e.target.value)}
                    disabled={busyLobby.login || busyLobby.register}
                  />
                  <input
                    type="password"
                    placeholder="회원가입 비밀번호"
                    value={registerPassword}
                    onChange={(e) => setRegisterPassword(e.target.value)}
                    disabled={busyLobby.login || busyLobby.register}
                  />
                  <input
                    placeholder="표시 이름(선택)"
                    value={registerDisplayName}
                    onChange={(e) => setRegisterDisplayName(e.target.value)}
                    disabled={busyLobby.login || busyLobby.register}
                  />
                  <div className="auth-actions single">
                    <button
                      onClick={() => {
                        void registerUser().catch((err: Error) => {
                          setStatus({ ok: false, text: "회원가입 실패: " + err.message });
                          alert(err.message);
                        });
                      }}
                      disabled={busyLobby.login || busyLobby.register}
                    >
                      {busyLobby.register ? "회원가입 중..." : "회원가입"}
                    </button>
                  </div>
                </div>
                <div className="auth-switch">
                  <button
                    onClick={() => setAuthScreen("login")}
                    disabled={busyLobby.login || busyLobby.register}
                  >
                    로그인 화면으로
                  </button>
                </div>
              </>
            )}
          </div>
        </section>
      ) : (
        <section className="lobby-only">
          <div className="lobby-card">
          <div className="lobby-head">
            <h2 className="card-title">방 리스트</h2>
            <button
              onClick={() => {
                void loadRooms().catch((err: Error) => {
                  setStatus({ ok: false, text: "방 목록 조회 실패: " + err.message });
                });
              }}
              disabled={!isAuthenticated || busyLobby.rooms}
            >
              {busyLobby.rooms ? "조회 중..." : "새로고침"}
            </button>
          </div>

          {!isAuthenticated ? (
            <p className="hint-text">로그인 후 방 목록을 확인할 수 있습니다.</p>
          ) : (
            <>
              <div className="room-list" aria-label="room-list">
                {rooms.map((room) => (
                  <button
                    key={room.roomId}
                    className={"room-item" + (selectedRoomId === room.roomId ? " active" : "")}
                    onClick={() => {
                      void selectRoom(room.roomId).catch((err: Error) => {
                        setStatus({ ok: false, text: "방 사용자 조회 실패: " + err.message });
                      });
                    }}
                  >
                    <span>{room.name}</span>
                    <small>{room.roomId}</small>
                  </button>
                ))}
              </div>

              <div className="room-users">
                <div className="room-users-head">
                  <strong>사용자 목록</strong>
                  <span>{busyLobby.users ? "불러오는 중..." : roomUsers.length + "명"}</span>
                </div>
                <ul>
                  {roomUsers.map((u) => (
                    <li key={u.userId}>{u.displayName} ({u.userId})</li>
                  ))}
                </ul>
              </div>

              <button
                className="join-room-button"
                disabled={!selectedRoomId || busy.join}
                onClick={() => {
                  void join().catch((err: Error) => {
                    setStatus({ ok: false, text: "Join failed: " + err.message });
                    alert(err.message);
                  });
                }}
              >
                {busy.join ? "참여 중..." : "참여 (Join)"}
              </button>
            </>
          )}
          </div>
        </section>
      )}

      {isAuthenticated && (
        <>
          <Controls
            roomId={roomId}
            userId={userId}
            busyJoin={busy.join}
            lockUserId={true}
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
            participantCount={participantCount}
            localUserId={localUserIdRef.current}
            readReceipts={readReceipts}
            quickEmojis={QUICK_EMOJIS}
            chatBoxRef={chatBoxRef}
            listEndRef={listEndRef}
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
            onSendFile={(file) => {
              void sendFile(file).catch((err: Error) => {
                setStatus({ ok: false, text: "File send failed: " + err.message });
                alert(err.message);
              });
            }}
          />
        </>
      )}
    </main>
  );
}
