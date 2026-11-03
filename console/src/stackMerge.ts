import type { StackMergeView } from "./types";

export const running = (j: StackMergeView) => j.status === "waiting" || j.status === "merging";

/** The most recent job (running first) that includes this PR. */
export const jobFor = (url: string, jobs: StackMergeView[] | undefined): StackMergeView | undefined =>
  (jobs ?? [])
    .filter((j) => j.prs.includes(url))
    .sort((a, b) => Number(running(b)) - Number(running(a)) || b.startedAt.localeCompare(a.startedAt))[0];

const num = (url: string) => `#${/\/pull\/(\d+)/.exec(url)?.[1] ?? url}`;

/** What the PR's item says about the job: `Waiting for checks on #12 (step 2 of 3)`, `Merged`, `Failed: <reason>`, ... */
export function stepLine(job: StackMergeView, url: string): string {
  const i = job.prs.indexOf(url);
  const step = `(step ${i + 1} of ${job.prs.length})`;
  if (job.status === "done" || i < job.index) return "Merged";
  if (i === job.index) {
    if (job.status === "waiting") return `Waiting for checks on ${num(url)} ${step}`;
    if (job.status === "merging") return `Merging ${num(url)} ${step}`;
    if (job.status === "failed") return `Failed: ${job.message ?? "unknown reason"}`;
    return "Cancelled";
  }
  return job.status === "failed" || job.status === "cancelled" ? "Not merged" : `Queued ${step}`;
}

/** The confirm text: `#13 → #12 → #11 into main, squash, waiting for checks between steps`. */
export const confirmLine = (topDownNumbers: number[], rootBase: string) =>
  `${topDownNumbers.map((n) => `#${n}`).join(" → ")} into ${rootBase || "the root's base"}, squash, waiting for checks between steps`;
