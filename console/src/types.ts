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
  /** Every repository attached to the session, and every branch it is on (older servers send only `repo` and `branch`). */
  repos?: string[];
  branches?: string[];
  createdAt: string | null;
  lastActivity: string | null;
  tags: string[];
  /** URLs of the pull requests the session owns (head is its branch). Only these accept PR actions. */
  pullRequests: string[];
  /** The subset of `pullRequests` owned only through a stack. */
  stackedPullRequests?: string[];
  /** The peasant has a background worker running (tag `fyrd-workers-running`): it can look idle yet still be working. */
  workersRunning?: boolean;
  cost?: HubCostView | null;
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
  /** The branch the PR merges into, and the one it merges from. */
  base?: string;
  head?: string;
  checksPassed: number;
  checksFailed: number;
  checksPending: number;
  /** The first failing check, with where to read why. */
  failingCheck: { name: string; url: string | null } | null;
  /** The head is in a fork, so it never links into a stack. */
  crossRepository?: boolean;
  /** The repo's default branch; a PR whose head is that branch never links into a stack. */
  defaultBranch?: string;
  /** Every failing check (older servers send only `failingCheck`). */
  failingChecks?: { name: string; url: string | null }[];
  review: "none" | "commented" | "approved" | "changes_requested";
  mergeable: "mergeable" | "conflicting" | "unknown";
  /** GitHub's `mergeStateStatus`, lower-cased. */
  mergeState: "clean" | "behind" | "blocked" | "dirty" | "unstable" | "draft" | "has_hooks" | "unknown";
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
  /** Set when a create succeeded but couldn't archive the previous Controller. */
  warning?: string | null;
}

/** Whether the server can talk to GitHub, and as whom. Never carries the token. */
export interface GitHubStatus {
  configured: boolean;
  login: string | null;
  error: string | null;
}

/** A running or just-finished stack merge (`stackMerges` in hub state). `prs` merge in the order listed: top of the chain first. */
export interface StackMergeView {
  id: string;
  repo: string;
  prs: string[];
  /** The PR being waited on or merged; every PR before it is merged. */
  index: number;
  status: "waiting" | "merging" | "done" | "failed" | "cancelled";
  message: string | null;
  startedAt: string;
  updatedAt: string;
}

export interface HubState {
  sessions: HubSessionView[];
  questions: HubQuestion[];
  signedOut: boolean;
  lastError: string | null;
  lastPollAt: string | null;
  controller: ControllerInfo | null;
  budget?: HubBudgetView | null;
  gitHub?: GitHubStatus;
  stackMerges?: StackMergeView[];
  /** Each owned PR's last listed status, by URL (held once however many sessions own it). */
  pullRequests?: Record<string, PullRequestStatus>;
  /** Peasant shared-memory proposals waiting for the owner. */
  pendingMemory?: number;
  /** Each session's current live-only progress line, kept by the server (so sessions nobody has open still have one). */
  progress?: Record<string, TranscriptItem>;
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
  kind: "text" | "question" | "answer" | "progress" | "worker" | "worker-result";
  text: string;
  at: string;
  questions?: TranscriptQuestion[] | null;
  toolUseId?: string | null;
  /** `worker` items: `text` is the worker's description, `detail` its subagent type. `worker-result` items: `text` is the worker's final reply (the `task_notification` summary), `status` how it ended, `toolUseId` links it to its `worker`. */
  /** Progress frames only: what the agent is doing now (a tool, "Thinking…"); `text` is the reply streamed so far. Both empty = nothing in progress. */
  status?: string | null;
  /** Progress frames only: the running tool's main argument, such as the Bash command. */
  detail?: string | null;
  /** Progress frames only: forked workers still running, each with its latest tool. */
  workers?: TranscriptWorker[] | null;
}

export interface TranscriptWorker {
  id: string;
  description: string;
  line?: string | null;
}

export interface HubPersona {
  id: string;
  name: string;
  model: string;
  definition: string;
  locked: boolean;
  seedHash?: string | null;
  updateAvailable?: boolean;
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

export interface PendingMemoryEntry {
  id: string;
  text: string;
  source: string;
  sessionId: string;
  at: string;
}

export interface HubMemory {
  scopes: MemoryScopeView[];
}

export interface AnswerBody {
  choice?: string;
  text?: string;
  /** Uploads attached to a typed answer. */
  fileIds?: string[];
}

export type SecretName = "anthropic-api-key" | "github-token";
/** Whether a value is set and where it comes from; the value itself never reaches the browser. */
export interface SecretView { name: SecretName; set: boolean; source: "saved" | "env" | "none" }
export interface SecretsView { supported: boolean; secrets: SecretView[] }
