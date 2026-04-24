export type Reaction = {
  emoji: string;
  count: number;
  users: string[];
};

export type ChatMessage = {
  sequence: number;
  userId: string;
  message: string;
  sentAt: string;
  clientMessageId: string;
  reactions: Reaction[];
};

export type Status = {
  ok: boolean;
  text: string;
};

export type BusyState = {
  join: boolean;
  send: boolean;
};
