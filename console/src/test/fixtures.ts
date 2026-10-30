import type { BoardSpec, FyrdConfig, InboxCard, SpecListEntry, SpecRun, Unit } from "../types";

export const card = (o: Partial<InboxCard> = {}): InboxCard => ({
  id: "c1", specId: "s1", unitId: null, question: "Which?", options: ["A", "B"],
  allowFreeText: false, createdAt: "2026-10-29T00:00:00Z", ...o,
});
export const run = (o: Partial<SpecRun> = {}): SpecRun => ({
  id: "s1", title: "Spec One", repo: "servicetitan/r", status: "Building",
  createdAt: "2026-10-29T00:00:00Z", costCents: 0, ...o,
});
export const entry = (o: Partial<SpecRun> = {}): SpecListEntry => {
  const r = run(o);
  return { run: r, id: r.id, error: null };
};
export const unit = (o: Partial<Unit> = {}): Unit => ({
  id: "u1", specId: "s1", title: "Unit One", stage: "Implement", state: "Running", ...o,
});
export const config = (o: Partial<FyrdConfig> = {}): FyrdConfig => ({
  squadCap: 3, environmentId: null, leadModel: "claude-opus-5-5", peerModel: "claude-sonnet-5-5", ...o,
});
export const boardSpec = (o: Partial<BoardSpec> = {}): BoardSpec => ({
  entry: entry(), units: [], lead: null, peers: [], ...o,
});
