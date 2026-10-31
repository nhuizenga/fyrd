import type {
  AnswerBody, Board, FyrdConfig, InboxCard, MemoryDoc, MemoryKind, NewSpec, Proposal, SpecDetail, SpecListEntry, SpecRun, WithRelay,
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

export const getBoard = () => call<Board>("GET", "/api/board");
export const getSpecs = () => call<SpecListEntry[]>("GET", "/api/specs");
export const getSpec = (id: string) => call<SpecDetail>("GET", `/api/specs/${enc(id)}`);
export const createSpec = (spec: NewSpec) => call<SpecRun>("POST", "/api/specs", spec);
export const attack = (id: string) => call<SpecRun>("POST", `/api/specs/${enc(id)}/attack`);
export const getInbox = () => call<InboxCard[]>("GET", "/api/inbox");
export const answer = (id: string, body: AnswerBody) =>
  call<WithRelay<InboxCard>>("POST", `/api/inbox/${enc(id)}/answer`, body);
export const getConfig = () => call<FyrdConfig>("GET", "/api/config");
export const putConfig = (config: FyrdConfig) => call<WithRelay<FyrdConfig>>("PUT", "/api/config", config);
export const research = (id: string) => call<SpecRun>("POST", `/api/specs/${enc(id)}/research`);
export const getProposal = (id: string) => call<Proposal>("GET", `/api/specs/${enc(id)}/proposal`);
export const acceptProposal = (id: string, markdown?: string) =>
  call<SpecRun>("POST", `/api/specs/${enc(id)}/proposal/accept`, markdown === undefined ? {} : { markdown });
export const discardProposal = (id: string) => call<SpecRun>("POST", `/api/specs/${enc(id)}/proposal/discard`);
export const updateMarkdown = (id: string, markdown: string) =>
  call<SpecRun>("PUT", `/api/specs/${enc(id)}/markdown`, { markdown });
export const getMemory = () => call<MemoryDoc>("GET", "/api/memory");
export const saveMemory = (kind: MemoryKind, text: string, repo?: string) =>
  call<void>("PUT", `/api/memory/${kind}${repo === undefined ? "" : `?repo=${enc(repo)}`}`, { text });
export const getUndelivered = () => call<InboxCard[]>("GET", "/api/inbox/undelivered");
export const relay = (id: string) => call<WithRelay<InboxCard>>("POST", `/api/inbox/${enc(id)}/relay`);
