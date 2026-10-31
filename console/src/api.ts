import type {
  AnswerBody, ControllerInfo, FyrdConfig, HubMemory, HubMessage, HubPersona, HubState, MemoryEntry, MemoryScope, WithRelay,
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
    throw new Error(detail ?? String(res.status));
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

const enc = encodeURIComponent;

export const getHubState = () => call<HubState>("GET", "/api/hub/state");
export const openController = () => call<ControllerInfo>("POST", "/api/hub/controller");
export const recreateController = () => call<ControllerInfo>("POST", "/api/hub/controller/recreate");
export const getMessages = (sessionId: string) =>
  call<HubMessage[]>("GET", `/api/hub/sessions/${enc(sessionId)}/messages`);
export const sendMessage = (sessionId: string, text: string) =>
  call<void>("POST", `/api/hub/sessions/${enc(sessionId)}/message`, { text });
export const pause = (sessionId: string) => call<void>("POST", `/api/hub/sessions/${enc(sessionId)}/pause`);
export const resume = (sessionId: string) => call<void>("POST", `/api/hub/sessions/${enc(sessionId)}/resume`);
export const answerQuestion = (id: string, body: AnswerBody) =>
  call<void>("POST", `/api/hub/questions/${enc(id)}/answer`, body);

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
