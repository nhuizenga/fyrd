import type { Page } from "@playwright/test";
import type { HubPersona, HubQuestion, MemoryEntry, HubSessionView, HubState, TranscriptItem } from "../src/types";

const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString();

export const session = (o: Partial<HubSessionView>): HubSessionView => ({
  id: "session_x", title: "builder-x", role: "builder", status: "idle", repo: null, branch: null,
  createdAt: minutesAgo(60 * 26), lastActivity: minutesAgo(5), tags: ["fyrd-hub", "fyrd-role-builder"], ...o,
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

export const QUESTION: HubQuestion = {
  id: "session_b2:req1:0", sessionId: "session_b2", sessionTitle: "builder-docs", question: "Which docs format?",
  options: ["Markdown", "AsciiDoc"], multiSelect: false, askedAt: minutesAgo(3), toolUseId: "tu_docs",
  requestId: "req1", input: {},
};

let seq = 0;
const item = (o: Partial<TranscriptItem>): TranscriptItem => {
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
  { id: "controller", name: "Controller", model: "claude-opus-5-5", definition: "Control.", locked: true },
  { id: "builder", name: "Builder", model: "claude-sonnet-5-5", definition: "Build.", locked: false },
];

const sse = (items: TranscriptItem[]) =>
  ": connected\n\n" + items.map((i) => `id: ${i.seq}\nevent: item\ndata: ${JSON.stringify(i)}\n\n`).join("");

export interface MockOptions {
  transcripts?: Record<string, TranscriptItem[]>;
  questions?: HubQuestion[];
  memory?: MemoryEntry[];
  /** Peasant shared-memory proposals waiting for approval. */
  pending?: { id: string; text: string; source: string; sessionId: string; at: string }[];
}

/** Serves a fake Fyrd API to the page and records the requests the console sends. */
export async function mockApi(page: Page, opts: MockOptions = {}) {
  const sent: { url: string; body: unknown }[] = [];
  const state: HubState = {
    sessions: SESSIONS, questions: opts.questions ?? [], signedOut: false, lastError: null,
    lastPollAt: new Date().toISOString(),
    budget: { date: "2026-11-01", timeZone: "UTC", fyrdCostToday: 120, fyrdLimitUsd: 500, fyrdBlocked: false },
    controller: { sessionId: "session_ctl", url: "https://claude.ai/code/session_ctl" },
    pendingMemory: opts.pending?.length ?? 0,
  };
  await page.route("**/api/**", async (route) => {
    const req = route.request();
    const url = new URL(req.url());
    const path = url.pathname;
    if (req.method() !== "GET") sent.push({ url: path, body: req.postData() ? req.postDataJSON() : null });
    const stream = /^\/api\/hub\/sessions\/([^/]+)\/stream$/.exec(path);
    if (stream) {
      return route.fulfill({
        status: 200, headers: { "content-type": "text/event-stream" }, body: sse(opts.transcripts?.[stream[1]] ?? []),
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
    if (path === "/api/hub/state") return route.fulfill({ json: state });
    if (/^\/api\/hub\/sessions\/[^/]+\/budget$/.test(path)) {
      return route.fulfill({ json: { costToday: 47.5, limitUsd: 75, ratio: 0.63, estimated: true, blocked: null, canExtend: false, exempt: false } });
    }
    if (path === "/api/hub/personas") return route.fulfill({ json: PERSONAS });
    if (/^\/api\/hub\/memory\/pending\/[^/]+\/approve$/.test(path)) return route.fulfill({ json: { id: "approved", text: "approved", source: "agent", at: new Date().toISOString() } });
    if (path === "/api/hub/memory/pending") return route.fulfill({ json: opts.pending ?? [] });
    if (path === "/api/hub/memory") return route.fulfill({ json: { scopes: [{ scope: "shared", label: "Shared", entries: opts.memory ?? [] }] } });
    if (path === "/api/config") {
      return route.fulfill({ json: { squadCap: 3, sessionCap: 20, environmentId: null, leadModel: "claude-opus-5-5", peerModel: "claude-sonnet-5-5", sessionDailyLimitUsd: 50, fyrdDailyLimitUsd: 500, timeZone: "UTC" } });
    }
    if (req.method() !== "GET") return route.fulfill({ status: 204 });
    return route.fulfill({ status: 404, json: { detail: "not mocked" } });
  });
  return { sent };
}
