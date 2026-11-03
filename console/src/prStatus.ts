import type { PullRequestStatus } from "./types";

/** How a PR (or the worst of several) looks on a session row, most urgent first. */
export type PrHealth = "failing" | "conflicts" | "pending" | "passing" | "none";

export const healthOf = (st: PullRequestStatus | undefined): PrHealth => {
  if (!st || st.state !== "open") return "none";
  if (st.checks === "failing") return "failing";
  if (st.mergeable === "conflicting") return "conflicts";
  if (st.checks === "pending") return "pending";
  return st.checks === "passing" ? "passing" : "none";
};

const ORDER: PrHealth[] = ["failing", "conflicts", "pending", "passing", "none"];

/** The worst of several PRs: failing > conflicts > pending > passing. */
export const worstHealth = (statuses: (PullRequestStatus | undefined)[]): PrHealth =>
  statuses.map(healthOf).sort((a, b) => ORDER.indexOf(a) - ORDER.indexOf(b))[0] ?? "none";

export const HEALTH_LABEL: Record<PrHealth, string> = {
  failing: "Checks failing", conflicts: "Merge conflicts", pending: "Checks running", passing: "Checks passing", none: "",
};

/** A chip surface colour for the PR's state (draft grey, open blue, merged purple, closed muted). */
export const stateColor = (st: PullRequestStatus | undefined): string | undefined =>
  !st || st.state === "unknown" ? undefined
  : st.state === "merged" ? "#ede9fe"
  : st.state === "closed" ? "#f3f4f6"
  : st.draft ? "#e5e7eb" : "#dbeafe";

/** Does any of these PRs have checks still running, so its status is worth re-reading soon? */
export const anyPending = (statuses: (PullRequestStatus | undefined)[]): boolean =>
  statuses.some((s) => s?.state === "open" && s.checks === "pending");
