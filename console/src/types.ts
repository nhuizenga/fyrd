export type MemoryScope = string;

export interface FyrdConfig {
  sessionCap: number;
  environmentId: string | null;
  /** Daily spend limit per session, in USD (1–10,000). */
  sessionDailyLimitUsd: number;
  /** Daily spend limit for the whole fyrd, in USD (1–10,000). */
  fyrdDailyLimitUsd: number;
  /** IANA time zone that defines "today" for spend; null until the console reports one. */
  timeZone: string | null;
}

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

/**
 * A session that ended its turn waiting on the owner through a plain-text question (no AskUserQuestion). `headline` is the
 * cloud's one-line summary of what it needs; `text` is the turn's last reply. One per turn: `id` is `{sessionId}:{turn}`.
 */
export interface HubNotification {
  id: string;
  sessionId: string;
  sessionTitle: string;
  headline: string;
  text: string | null;
  at: string;
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
  /** Absent from older servers. */
  notifications?: HubNotification[];
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

/** A persona as the repo defines it (`skills/<id>/SKILL.md`), with the owner's model choice in `model`. */
export interface HubPersona {
  id: string;
  name: string;
  model: string;
  /** The model the repo gives this persona; `model` differs when the owner chose another. */
  defaultModel?: string | null;
  definition: string;
  locked: boolean;
}

/** Text a role's sessions load on demand with the Skill tool. `roles` are persona ids. */
export interface SessionSkill {
  name: string;
  /** One line: what the skill holds and when to load it. */
  description: string;
  roles: string[];
  body: string;
  /** Set for a skill that came from a default; such a skill can be reset. */
  seedHash?: string | null;
  updateAvailable?: boolean;
  /** Its description and body are its default's, the text the fyrd plugin ships: a session with the plugin loads it as `fyrd:<name>`. */
  inPlugin?: boolean;
}

/**
 * How sessions get their session skills: `inline` (the plugin setup script is not confirmed: the whole text is in their
 * instructions) or `installed` (the environment installs the fyrd plugin: they load the defaults from it on demand, and
 * get the whole text of skills edited or added in the console).
 */
export type SessionSkillMode = "inline" | "installed";

export interface SessionSkillSetup {
  mode: SessionSkillMode;
  /** The owner confirmed the block is in the environment's setup script. */
  confirmed: boolean;
  environmentId: string | null;
  skills: string[];
  /** The skills sessions load from the plugin, by the name they load them by (`fyrd:<name>`). */
  pluginSkills?: string[];
  /** Skills edited or added in the console: sessions always get their whole text. */
  inlineSkills?: string[];
  /** Defaults the owner deleted; resetting one by name brings it back. */
  missingDefaults?: string[];
  /** The block to paste into the environment's setup script. It is the same for any skills. */
  script: string;
  /** The GitHub repository the block installs the plugin from, as owner/repo. */
  repository: string;
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
