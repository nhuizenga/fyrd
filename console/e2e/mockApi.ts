import type { Page } from "@playwright/test";
import type { HubPersona, HubQuestion, MemoryEntry, HubSessionView, HubState, PullRequestStatus, StackMergeView, TranscriptItem } from "../src/types";

const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString();

export const session = (o: Partial<HubSessionView>): HubSessionView => ({
  id: "session_x", title: "builder-x", role: "builder", status: "idle", repo: null, branch: null,
  createdAt: minutesAgo(60 * 26), lastActivity: minutesAgo(5), pullRequests: [], tags: ["fyrd-hub", "fyrd-role-builder"], ...o,
});

export const SESSIONS: HubSessionView[] = [
  session({ id: "session_ctl", title: "Controller role", role: "controller", tags: [] }),
  session({
    id: "session_b1", title: "builder-prime-sieve", status: "running", repo: "acme/sieve",
    cost: { costToday: 47.5, limitUsd: 50, ratio: 0.95, estimated: true, blocked: null, canExtend: true, exempt: false },
  }),
  session({ id: "session_b2", title: "builder-docs", status: "requires_action" }),
  session({ id: "session_b3", title: "reviewer-api", role: "reviewer" }),
  session({ id: "session_b4", title: "validator-web", role: "validator", status: "archived" }),
];

const PR = (n: number) => `https://github.com/acme/stack/pull/${n}`;
const stackPr = (n: number, head: string, base: string): PullRequestStatus => ({
  url: PR(n), number: n, title: `Stacked change ${n}`, state: "open", draft: false, checks: "passing", canMerge: true, head, base,
  checksPassed: 3, checksFailed: 0, checksPending: 0, failingCheck: null, review: "none", mergeable: "mergeable", mergeState: "clean",
});

/** A builder whose three open PRs are stacked: main ← #11 ← #12 ← #13 (top). */
export const STACK_SESSION = session({
  id: "session_stack", title: "builder-stack", repo: "acme/stack", branch: "fyrd/b",
  pullRequests: [PR(11), PR(12), PR(13)], stackedPullRequests: [PR(11), PR(13)],
});
export const STACK_PRS: PullRequestStatus[] = [stackPr(11, "fyrd/a", "main"), stackPr(12, "fyrd/b", "fyrd/a"), stackPr(13, "fyrd/c", "fyrd/b")];
export const prStatus = (o: Partial<PullRequestStatus> & { number: number }): PullRequestStatus => ({
  url: `https://github.com/acme/sieve/pull/${o.number}`, title: `PR ${o.number}`, state: "open", draft: false, checks: "passing",
  canMerge: true, base: "main", head: `fyrd/pr-${o.number}`, checksPassed: 3, checksFailed: 0, checksPending: 0, failingCheck: null,
  review: "none", mergeable: "mergeable", mergeState: "clean", ...o,
});

export const LONG_TITLE = "Draft with mixed checks and a very long title that must truncate nicely in the details pane column width";

export const QUESTION: HubQuestion = {
  id: "session_b2:req1:0", sessionId: "session_b2", sessionTitle: "builder-docs", question: "Which docs format?",
  options: ["Markdown", "AsciiDoc"], multiSelect: false, askedAt: minutesAgo(3), toolUseId: "tu_docs",
  requestId: "req1", input: {},
};

let seq = 0;
export const item = (o: Partial<TranscriptItem>): TranscriptItem => {
  seq += 1;
  return { key: `${seq}:0`, seq, role: "agent", kind: "text", text: "", at: minutesAgo(1), ...o };
};

/** A long transcript (so the thread must scroll) with markdown in it. */
export const longTranscript = (): TranscriptItem[] => [
  item({ role: "user", text: "Build a prime sieve." }),
  ...Array.from({ length: 30 }, (_, i) =>
    item({ text: `Step ${i + 1}: **progress** with \`code_${i}\` and a [link](https://example.com).` })),
  item({ text: "## Done\n\n| n | primes |\n|---|---|\n| 10 | 4 |" }),
];

export const docsTranscript = (): TranscriptItem[] => [
  item({ role: "user", text: "Write the docs." }),
  item({ text: "Line one of the agent's reply.\n\nLine two.\n\nLine three.\n\nLine four.\n\nLine five, the latest." }),
  item({
    kind: "question", text: "Which docs format?", toolUseId: "tu_docs",
    questions: [{ question: "Which docs format?", multiSelect: false, options: [{ label: "Markdown" }, { label: "AsciiDoc" }] }],
  }),
];

const PERSONAS: HubPersona[] = [
  { id: "controller", name: "Controller", model: "claude-opus-5-5", defaultModel: "claude-opus-5-5", definition: "Control.", locked: true },
  { id: "builder", name: "Builder", model: "claude-sonnet-5-5", defaultModel: "claude-sonnet-5-5", definition: "Build.", locked: false },
  { id: "reviewer", name: "Reviewer", model: "claude-opus-5-5", defaultModel: "claude-sonnet-5-5", definition: "Review.", locked: false },
];

const sse = (items: TranscriptItem[]) =>
  ": connected\n\n" + items.map((i) => `id: ${i.seq}\nevent: item\ndata: ${JSON.stringify(i)}\n\n`).join("");

export interface MockOptions {
  transcripts?: Record<string, TranscriptItem[]>;
  questions?: HubQuestion[];
  memory?: MemoryEntry[];
  /** Adds a builder with a three-PR stack, and serves its PR statuses. */
  stack?: boolean;
  stackMerges?: StackMergeView[];
  /** Pull requests of `session_b1` (the session lists their URLs and the API serves these statuses). */
  prs?: PullRequestStatus[];
  /** Hold `POST …/message` this long (and optionally fail it) to stand in for a slow upstream. */
  message?: { delayMs: number; status?: number };
  /** Peasant shared-memory proposals waiting for approval. */
  pending?: { id: string; text: string; source: string; sessionId: string; at: string }[];
  /** Session rows to serve instead of the defaults. */
  sessions?: HubSessionView[];
  /** A live `progress` frame per session, sent after its items. */
  progress?: Record<string, TranscriptItem>;
}

/** Serves a fake Fyrd API to the page and records the requests the console sends. */
export async function mockApi(page: Page, opts: MockOptions = {}) {
  const sent: { url: string; body: unknown }[] = [];
  let uploads = 0;
  const state: HubState = {
    sessions: [...(opts.sessions ?? SESSIONS), ...(opts.stack ? [STACK_SESSION] : [])].map((x) => (opts.prs && x.id === "session_b1" ? { ...x, pullRequests: opts.prs.map((p) => p.url) } : x)), questions: opts.questions ?? [], signedOut: false, lastError: null,
    lastPollAt: new Date().toISOString(),
    budget: { date: "2026-11-01", timeZone: "UTC", fyrdCostToday: 120, fyrdLimitUsd: 500, fyrdBlocked: false },
    controller: { sessionId: "session_ctl", url: "https://claude.ai/code/session_ctl" },
    pendingMemory: opts.pending?.length ?? 0,
    gitHub: { configured: true, login: "octocat", error: null },
    pullRequests: opts.stack ? Object.fromEntries(STACK_PRS.map((p) => [p.url, p])) : {},
    stackMerges: opts.stackMerges ?? [],
  };
  await page.route("**/api/**", async (route) => {
    const req = route.request();
    const url = new URL(req.url());
    const path = url.pathname;
    // Uploads are multipart (not JSON): record the file name and hand back an id.
    if (req.method() === "POST" && path === "/api/hub/uploads") {
      const name = /filename="([^"]*)"/.exec(req.postData() ?? "")?.[1] ?? "file";
      sent.push({ url: path, body: { name } });
      return route.fulfill({ json: { id: `file_${(uploads += 1)}`, name, mimeType: "text/plain", size: 5 } });
    }
    if (req.method() !== "GET") sent.push({ url: path, body: req.postData() ? req.postDataJSON() : null });
    const stream = /^\/api\/hub\/sessions\/([^/]+)\/stream$/.exec(path);
    if (stream) {
      const after = Number(url.searchParams.get("after") ?? -1); // like the server: resume after this seq
      return route.fulfill({
        status: 200, headers: { "content-type": "text/event-stream" }, body: sse((opts.transcripts?.[stream[1]] ?? []).filter((i) => i.seq > after))
          + (opts.progress?.[stream[1]] ? `event: progress\ndata: ${JSON.stringify(opts.progress[stream[1]])}\n\n` : ""),
      });
    }
    const exp = /^\/api\/hub\/sessions\/([^/]+)\/export$/.exec(path);
    if (exp) {
      return route.fulfill({
        status: 200,
        headers: {
          "content-type": "application/x-ndjson", "content-disposition": `attachment; filename="export-${exp[1]}-20261102-201000.jsonl"`,
        },
        body: `{"type":"fyrd_export","sessionId":"${exp[1]}"}\n{"type":"fyrd_export_end","eventCount":0}\n`,
      });
    }
    if (/^\/api\/hub\/sessions\/session_stack\/pull-requests$/.test(path)) return route.fulfill({ json: STACK_PRS });
    if (opts.prs && path === "/api/hub/sessions/session_b1/pull-requests") return route.fulfill({ json: opts.prs });
    if (opts.message && req.method() === "POST" && /\/message$/.test(path)) {
      await new Promise((r) => setTimeout(r, opts.message!.delayMs));
      return route.fulfill(opts.message.status && opts.message.status >= 400
        ? { status: opts.message.status, json: { detail: "Upstream down" } }
        : { status: 204 });
    }
    if (path === "/api/hub/state") return route.fulfill({ json: state });
    if (/^\/api\/hub\/sessions\/[^/]+\/budget$/.test(path)) {
      return route.fulfill({ json: { costToday: 47.5, limitUsd: 75, ratio: 0.63, estimated: true, blocked: null, canExtend: false, exempt: false } });
    }
    if (path === "/api/hub/personas") return route.fulfill({ json: PERSONAS });
    const persona = /^\/api\/hub\/personas\/([^/]+)(\/reset)?$/.exec(path);
    if (persona && req.method() !== "GET") {
      const p = PERSONAS.find((x) => x.id === persona[1])!;
      return route.fulfill({ json: { ...p, model: persona[2] ? p.defaultModel : (req.postDataJSON() as { model: string }).model } });
    }
    if (path === "/api/hub/session-skills") return route.fulfill({ json: [] });
    if (path === "/api/hub/session-skills/setup-script") {
      return route.fulfill({ json: {
        mode: "inline", confirmed: false, environmentId: null, skills: [], pluginSkills: [], inlineSkills: [], missingDefaults: [],
        script: "# >>> fyrd plugin >>>\n# <<< fyrd plugin <<<\n", repository: "nhuizenga/fyrd",
      } });
    }
    if (/^\/api\/hub\/memory\/pending\/[^/]+\/approve$/.test(path)) return route.fulfill({ json: { id: "approved", text: "approved", source: "agent", at: new Date().toISOString() } });
    if (path === "/api/hub/memory/pending") return route.fulfill({ json: opts.pending ?? [] });
    if (path === "/api/hub/memory") return route.fulfill({ json: { scopes: [{ scope: "shared", label: "Shared", entries: opts.memory ?? [] }] } });
    if (path === "/api/session-order") return route.fulfill({ json: { sessions: [], groups: [] } });
    if (path === "/api/config") {
      return route.fulfill({ json: { sessionCap: 20, environmentId: null, sessionDailyLimitUsd: 50, fyrdDailyLimitUsd: 500, timeZone: "UTC" } });
    }
    if (req.method() !== "GET") return route.fulfill({ status: 204 });
    return route.fulfill({ status: 404, json: { detail: "not mocked" } });
  });
  return { sent };
}
