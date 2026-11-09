import type {
  AnswerBody, ControllerInfo, FyrdConfig, HubCostView, HubMemory, HubPersona, HubState, MemoryEntry, MemoryScope, PendingMemoryEntry, PullRequestStatus, WithRelay,
} from "./types";

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(path, {
    method,
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    let detail: string | undefined;
    try {
      detail = (await res.json())?.detail;
    } catch {
      /* non-JSON error body */
    }
    // The status lets callers tell "not found" from other failures.
    throw Object.assign(new Error(detail ?? String(res.status)), { status: res.status });
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

const enc = encodeURIComponent;

export const getHubState = () => call<HubState>("GET", "/api/hub/state");
/** The live adopted Controller; rejects with status 404 when there is none. */
export const openController = () => call<ControllerInfo>("POST", "/api/hub/controller");
/** The owner creates the Controller on claude.ai; Fyrd adopts it by id (Fyrd-created sessions get no connectors). */
export const adoptController = (sessionId: string) =>
  call<ControllerInfo>("POST", "/api/hub/controller/adopt", { sessionId });
/** Archives the current Controller; the owner then adopts a new one. */
export const retireController = () => call<void>("POST", "/api/hub/controller/retire");
/** `fileIds` are uploads from `uploadFile`; the text may be empty when files are attached. */
export const sendMessage = (sessionId: string, text: string, fileIds?: string[]) =>
  call<void>("POST", `/api/hub/sessions/${enc(sessionId)}/message`, fileIds?.length ? { text, fileIds } : { text });
export interface UploadedFile { id: string; name: string; mimeType: string; size: number }
/** Uploads one attachment (the runtime puts it in the Files API for an hour); send its `id` with the message or answer. */
export const uploadFile = async (file: File): Promise<UploadedFile> => {
  const form = new FormData();
  form.append("file", file, file.name);
  const res = await fetch("/api/hub/uploads", { method: "POST", body: form });
  if (!res.ok) {
    let detail: string | undefined;
    try { detail = (await res.json())?.detail; } catch { /* non-JSON error body */ }
    throw Object.assign(new Error(detail ?? `Upload failed (${res.status})`), { status: res.status });
  }
  return (await res.json()) as UploadedFile;
};
/** Forgets an upload the owner removed before sending. */
export const deleteUpload = (fileId: string) => call<void>("DELETE", `/api/hub/uploads/${enc(fileId)}`);
export const pause = (sessionId: string) => call<void>("POST", `/api/hub/sessions/${enc(sessionId)}/pause`);
/** Renames a session (the pencil button); the new title shows on the next poll. */
export const renameSession = (sessionId: string, title: string) =>
  call<void>("POST", `/api/hub/sessions/${enc(sessionId)}/rename`, { title });

/** Archives a session (the trash button); it leaves the list on the next poll. */
/** URL of a session's raw-event export (JSONL). A URL, not a `call`, because the browser handles the download. */
export const exportUrl = (sessionId: string) => `/api/hub/sessions/${enc(sessionId)}/export`;
export const archiveSession = (sessionId: string) => call<void>("POST", `/api/hub/sessions/${enc(sessionId)}/archive`);
export const pullRequests = (sessionId: string) =>
  call<PullRequestStatus[]>("GET", `/api/hub/sessions/${enc(sessionId)}/pull-requests`);
export const markPrReady = (sessionId: string, url: string) =>
  call<void>("POST", `/api/hub/sessions/${enc(sessionId)}/pull-requests/ready`, { url });
/** `force` skips the stack merge-order guard ("Merge this only anyway"); the server refuses a PR with PRs stacked on it without it. */
export const mergePr = (sessionId: string, url: string, force = false) =>
  call<void>("POST", `/api/hub/sessions/${enc(sessionId)}/pull-requests/merge`, force ? { url, force: true } : { url });
/** Starts a job that merges the whole chain the PR is in, top down, waiting for checks between steps. */
export const mergeStack = (sessionId: string, url: string) =>
  call<{ jobId: string }>("POST", `/api/hub/sessions/${enc(sessionId)}/pull-requests/merge-stack`, { url });
export const cancelStackMerge = (jobId: string) => call<void>("POST", `/api/hub/stack-merges/${enc(jobId)}/cancel`);
export const resume = (sessionId: string) => call<void>("POST", `/api/hub/sessions/${enc(sessionId)}/resume`);
/** Raise one session's limit for today; the server accepts 25 or 100 once the session is at 90% of its limit. */
export const addBudget = (sessionId: string, amountUsd: 25 | 100) =>
  call<HubCostView>("POST", `/api/hub/sessions/${enc(sessionId)}/budget`, { amountUsd });
/** 202 with `{ waitingFor }` while sibling questions of the same request are unanswered; 204 (undefined) once sent. */
export const answerQuestion = (id: string, body: AnswerBody) =>
  call<{ waitingFor: number } | undefined>("POST", `/api/hub/questions/${enc(id)}/answer`, body);

export const getPersonas = () => call<HubPersona[]>("GET", "/api/hub/personas");
export const savePersona = (p: HubPersona) => call<HubPersona>("PUT", `/api/hub/personas/${enc(p.id)}`, p);
export const resetPersona = (id: string) => call<HubPersona>("POST", `/api/hub/personas/${enc(id)}/reset`);
export const deletePersona = (id: string) => call<void>("DELETE", `/api/hub/personas/${enc(id)}`);

/** Shared-memory proposals from peasants, newest first, waiting for the owner. */
export const getPendingMemory = () => call<PendingMemoryEntry[]>("GET", "/api/hub/memory/pending");
/** Approves into shared memory, as proposed or with edited text. */
export const approvePendingMemory = (id: string, text?: string) =>
  call<MemoryEntry>("POST", `/api/hub/memory/pending/${enc(id)}/approve`, text === undefined ? {} : { text });
export const rejectPendingMemory = (id: string) => call<void>("POST", `/api/hub/memory/pending/${enc(id)}/reject`);
export const getHubMemory = () => call<HubMemory>("GET", "/api/hub/memory");
export const addMemory = (scope: MemoryScope, text: string) =>
  call<MemoryEntry>("POST", `/api/hub/memory/${enc(scope)}`, { text });
export const editMemory = (scope: MemoryScope, entryId: string, text: string) =>
  call<void>("PUT", `/api/hub/memory/${enc(scope)}/${enc(entryId)}`, { text });
export const deleteMemory = (scope: MemoryScope, entryId: string) =>
  call<void>("DELETE", `/api/hub/memory/${enc(scope)}/${enc(entryId)}`);

let lastConfig: FyrdConfig | null = null;

export const getConfig = async () => {
  lastConfig = await call<FyrdConfig>("GET", "/api/config");
  return lastConfig;
};
/** PUT /api/config replaces the whole config, so always send a complete object. */
export const putConfig = async (config: FyrdConfig) => {
  const saved = await call<WithRelay<FyrdConfig>>("PUT", "/api/config", config);
  lastConfig = saved;
  return saved;
};
/** Merge a partial change over the last loaded config (loading it first if needed) and save the result. */
export const updateConfig = async (patch: Partial<FyrdConfig>) => {
  const base = lastConfig ?? (await getConfig());
  return putConfig({ ...base, ...patch });
};
