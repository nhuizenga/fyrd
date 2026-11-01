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

export interface HubMessage {
  role: "user" | "agent";
  text: string;
  at: string;
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
