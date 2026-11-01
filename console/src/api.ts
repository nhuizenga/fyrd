import type {
  AnswerBody, ControllerInfo, FyrdConfig, HubMemory, HubPersona, HubState, MemoryEntry, MemoryScope, WithRelay,
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
export const sendMessage = (sessionId: string, text: string) =>
  call<void>("POST", `/api/hub/sessions/${enc(sessionId)}/message`, { text });
export const pause = (sessionId: string) => call<void>("POST", `/api/hub/sessions/${enc(sessionId)}/pause`);
export const resume = (sessionId: string) => call<void>("POST", `/api/hub/sessions/${enc(sessionId)}/resume`);
/** 202 with `{ waitingFor }` while sibling questions of the same request are unanswered; 204 (undefined) once sent. */
export const answerQuestion = (id: string, body: AnswerBody) =>
  call<{ waitingFor: number } | undefined>("POST", `/api/hub/questions/${enc(id)}/answer`, body);

export const getPersonas = () => call<HubPersona[]>("GET", "/api/hub/personas");
export const savePersona = (p: HubPersona) => call<HubPersona>("PUT", `/api/hub/personas/${enc(p.id)}`, p);
export const deletePersona = (id: string) => call<void>("DELETE", `/api/hub/personas/${enc(id)}`);

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
