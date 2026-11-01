import type { FyrdConfig, HubQuestion, HubSessionView, HubState } from "../types";

export const question = (o: Partial<HubQuestion> = {}): HubQuestion => ({
  id: "s1:r1:0", sessionId: "s1", sessionTitle: "builder-ab12", question: "Which DB?", options: ["Postgres", "SQLite"],
  multiSelect: false, askedAt: "2026-10-31T00:00:00Z", toolUseId: "t1", requestId: "r1", input: {}, ...o,
});
export const session = (o: Partial<HubSessionView> = {}): HubSessionView => ({
  id: "s1", title: "builder-ab12", role: "builder", status: "running", repo: null, branch: null,
  createdAt: "2026-10-31T00:00:00Z", lastActivity: null, tags: ["fyrd-hub"], pullRequests: [], ...o,
});
export const hubState = (o: Partial<HubState> = {}): HubState => ({
  sessions: [], questions: [], signedOut: false, lastError: null, lastPollAt: null, controller: null, ...o,
});
export const config = (o: Partial<FyrdConfig> = {}): FyrdConfig => ({
  squadCap: 3, sessionCap: 20, environmentId: null, leadModel: "claude-opus-5-5", peerModel: "claude-sonnet-5-5", ...o,
});
