export type SpecStatus =
  | "Draft" | "Queued" | "Researching" | "Building"
  | "FinalReview" | "ReadyForHandoff" | "PrOpened" | "Failed";
export type UnitStage = "Research" | "Implement" | "Review" | "Validate" | "Done";
export type UnitState = "Running" | "Idle" | "WaitingOnOwner" | "Blocked" | "Done";
export type MemoryScope = "Repo" | "Global";

export interface SpecRun {
  id: string;
  title: string;
  repo: string;
  status: SpecStatus;
  createdAt: string;
  sessionId?: string | null;
  costCents: number;
  branch?: string | null;
  error?: string | null;
  hasProposal?: boolean;
}

export interface Unit {
  id: string;
  specId: string;
  title: string;
  stage: UnitStage;
  state: UnitState;
}

export interface SpecListEntry {
  run: SpecRun | null;
  id: string;
  error: string | null;
}

export interface CardAnswer {
  text: string;
  remember: boolean;
  scope: MemoryScope;
  answeredAt: string;
  relayStatus?: "pending" | "sent" | "failed" | null;
  relayAttempts?: number;
  relayError?: string | null;
}

export interface InboxCard {
  id: string;
  specId: string;
  unitId?: string | null;
  question: string;
  options: string[];
  recommendation?: string | null;
  reason?: string | null;
  details?: string | null;
  allowFreeText: boolean;
  createdAt: string;
  answer?: CardAnswer | null;
  sourceKey?: string | null;
}

export interface FyrdConfig {
  squadCap: number;
  environmentId: string | null;
  leadModel: string;
  peerModel: string;
}

/** Mutating responses may carry a relay failure alongside the saved value. */
export type WithRelay<T> = T & { relayError?: string };

export interface LeadView {
  sessionId: string;
  url: string;
  status: string | null;
  contextUsed: number | null;
  contextMax: number | null;
}

export interface PeerView {
  sessionId: string;
  url: string;
  role: string;
  unitId: string | null;
  status: string | null;
}

export interface BoardSpec {
  entry: SpecListEntry;
  units: Unit[];
  lead: LeadView | null;
  peers: PeerView[];
}

export interface Board {
  specs: BoardSpec[];
  signedOut: boolean;
  lastError: string | null;
  lastPollAt: string | null;
  rateLimit: { status: string | null; resetsAt: string | null } | null;
}

export interface SpecDetail {
  run: SpecRun;
  markdown: string;
  units: Unit[];
}

export interface AnswerBody {
  text: string;
  remember: boolean;
  scope: MemoryScope;
}

export interface NewSpec {
  title: string;
  repo: string;
  markdown: string;
}

export interface Proposal {
  markdown: string;
  summary: string;
  openQuestions: string[];
  sessionId: string | null;
}

export interface MemoryDoc {
  ownerProxy: string;
  global: string;
  repos: { repo: string; text: string }[];
}

export type MemoryKind = "owner-proxy" | "global" | "repo";
