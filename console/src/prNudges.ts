import type { HubSessionView, PullRequestStatus } from "./types";

export type NudgeKind = "validate" | "review" | "watch" | "fix";

/** One thing the owner can ask a session to do about a PR, as an ordinary owner message. */
export interface Nudge {
  kind: NudgeKind;
  label: string;
  /** The session the message goes to. Null when there is nobody to send it to (the item is then disabled). */
  to: HubSessionView | null;
  text: string;
  /** Set when the item is shown but cannot be used. */
  disabledReason?: string;
  /** A one-line confirm before sending (the nudge starts a paid session). */
  confirm?: string;
}

/**
 * The builder that owns a PR: a `builder` session whose PRs include it. When several do, the one on the PR's head branch
 * wins. Null when none does.
 */
export const owningBuilder = (url: string, head: string | undefined, sessions: HubSessionView[]): HubSessionView | null => {
  const builders = sessions.filter((s) => s.role === "builder" && s.status !== "archived" && s.pullRequests.includes(url));
  return builders.find((s) => head !== undefined && head !== "" && s.branch === head) ?? builders[0] ?? null;
};

const failingNames = (st: PullRequestStatus): string => {
  const names = (st.failingChecks?.length ? st.failingChecks : st.failingCheck ? [st.failingCheck] : []).map((c) => c.name);
  return names.length > 0 ? names.join(", ") : "see the PR";
};

/**
 * The nudges that apply to a PR.
 * - Request validation: open, to the Controller.
 * - Request review: open and not a draft, to the Controller.
 * - Watch PR: open, to the Controller (asks first: it starts a paid session).
 * - Fix failing checks: checks failing, or conflicting, to the owning Builder (hidden when there is none).
 * The Controller items are disabled with "No Controller" when there is no Controller.
 */
export function nudgesFor(
  st: PullRequestStatus, sessions: HubSessionView[], controller: HubSessionView | null,
): Nudge[] {
  if (st.state !== "open") return [];
  const out: Nudge[] = [];
  const base = st.base || "the repo's default branch";
  const ctl = (n: Omit<Nudge, "to" | "disabledReason">): Nudge =>
    controller ? { ...n, to: controller } : { ...n, to: null, disabledReason: "No Controller" };
  const builder = owningBuilder(st.url, st.head, sessions);

  out.push(ctl({
    kind: "validate", label: "Request validation",
    text: `Validate PR ${st.url}. Base branch: ${base}.${builder ? ` Builder: ${builder.title} (${builder.id}).` : ""}`,
  }));
  if (!st.draft) out.push(ctl({ kind: "review", label: "Request review", text: `PR #${st.number} is ready: ${st.url}` }));
  out.push(ctl({
    kind: "watch", label: "Watch PR", text: `Watch ${st.url} and fix anything that comes up`,
    confirm: `Start a PR Monitor for #${st.number}?`,
  }));

  const conflicting = st.mergeable === "conflicting";
  if ((st.checks === "failing" || conflicting) && builder) {
    const text = conflicting
      ? `${st.url} has merge conflicts with ${base}. Merge ${base} in (no force-push), fix, run the tests, push, and reply as before.`
      : `Checks are failing on ${st.url}: ${failingNames(st)}. Fix them, run the tests, push, and reply as before.`;
    out.push({ kind: "fix", label: "Fix failing checks", to: builder, text });
  }
  return out;
}
