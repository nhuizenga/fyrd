export type MemoryScope = string;

export interface FyrdConfig {
  squadCap: number;
  sessionCap: number;
  environmentId: string | null;
  leadModel: string;
  peerModel: string;
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
  /** URLs of the pull requests the session opened. */
  pullRequests: string[];
}

/** One of a session's pull requests with live GitHub status (`GET /api/hub/sessions/{id}/pull-requests`). */
export interface PullRequestStatus {
  url: string;
  number: number;
  title: string;
  state: "open" | "merged" | "closed" | "unknown";
  draft: boolean;
  checks: "none" | "pending" | "passing" | "failing";
  /** Why GitHub could not be read, when `state` is `unknown`. */
  error?: string | null;
  canMerge: boolean;
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

/** One frame of `GET /api/hub/sessions/{id}/stream` (`event: item`). Live-only `event: progress` frames reuse the shape with `kind: "progress"`. */
export interface TranscriptItem {
  key: string;
  seq: number;
  role: "user" | "agent";
  kind: "text" | "question" | "answer" | "progress";
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
