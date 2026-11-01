export type MemoryScope = string;

export interface FyrdConfig {
  squadCap: number;
  sessionCap: number;
  environmentId: string | null;
  leadModel: string;
  peerModel: string;
  /** Daily spend limit per session, in USD (1–10,000). */
  sessionDailyLimitUsd: number;
  /** Daily spend limit for the whole fyrd, in USD (1–10,000). */
  fyrdDailyLimitUsd: number;
  /** IANA time zone that defines "today" for spend; null until the console reports one. */
  timeZone: string | null;
}

/** Mutating responses may carry a relay failure alongside the saved value. */
export type WithRelay<T> = T & { relayError?: string };

export interface HubSessionView {
  id: string;
  title: string;
  role: string;
  status: string;
  repo: string | null;
  branch: string | null;
  createdAt: string | null;
  lastActivity: string | null;
  tags: string[];
  cost?: HubCostView | null;
}

/** A session's estimated (list-price) spend for today against its daily limit. */
export interface HubCostView {
  costToday: number;
  limitUsd: number;
  ratio: number;
  estimated: boolean;
  blocked: "session" | "fyrd" | null;
  canExtend: boolean;
  exempt: boolean;
}

export interface HubBudgetView {
  date: string;
  timeZone: string;
  fyrdCostToday: number;
  fyrdLimitUsd: number;
  fyrdBlocked: boolean;
}

export interface HubQuestion {
  id: string;
  sessionId: string;
  sessionTitle: string;
  question: string;
  options: string[];
  multiSelect: boolean;
  askedAt: string;
  toolUseId: string;
  requestId: string;
  input: unknown;
}

export interface ControllerInfo {
  sessionId: string;
  url: string;
}

export interface HubState {
  sessions: HubSessionView[];
  questions: HubQuestion[];
  signedOut: boolean;
  lastError: string | null;
  lastPollAt: string | null;
  controller: ControllerInfo | null;
  budget?: HubBudgetView | null;
}

export interface TranscriptOption {
  label: string;
  description?: string | null;
}

export interface TranscriptQuestion {
  question: string;
  header?: string | null;
  multiSelect: boolean;
  options: TranscriptOption[];
}

/** One frame of `GET /api/hub/sessions/{id}/stream` (`event: item`). */
export interface TranscriptItem {
  key: string;
  seq: number;
  role: "user" | "agent";
  kind: "text" | "question" | "answer";
  text: string;
  at: string;
  questions?: TranscriptQuestion[] | null;
  toolUseId?: string | null;
}

export interface HubPersona {
  id: string;
  name: string;
  model: string;
  definition: string;
  locked: boolean;
}

export interface MemoryEntry {
  id: string;
  text: string;
  source: string;
  at: string;
}

export interface MemoryScopeView {
  scope: MemoryScope;
  label: string;
  entries: MemoryEntry[];
}

export interface HubMemory {
  scopes: MemoryScopeView[];
}

export interface AnswerBody {
  choice?: string;
  text?: string;
}
