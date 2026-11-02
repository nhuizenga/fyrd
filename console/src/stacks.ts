import type { PullRequestStatus } from "./types";

/** A chain of open PRs, each stacked on the one below it. */
export interface Chain {
  /** From the root (its base is not another of these PRs' heads) to the top. */
  prs: PullRequestStatus[];
  /** Where the root merges into (usually the default branch). */
  rootBase: string;
  /** Some PR has more than one open child: shown, but a stack merge is not allowed. */
  fork: boolean;
}

const open = (prs: PullRequestStatus[]) => prs.filter((p) => p.state === "open");

const repoOf = (url: string) => url.slice(0, url.lastIndexOf("/pull/")).toLowerCase();

/**
 * PR `child` is stacked on `parent` when its base is the parent's head, in the same repo, neither head is in a fork, and the
 * parent's head is not the default branch (PRs that merely target `main` are not stacked with each other).
 */
export const isStackedOn = (child: PullRequestStatus, parent: PullRequestStatus): boolean =>
  child.url !== parent.url && !!child.base && child.base === parent.head
  && !parent.crossRepository && !child.crossRepository
  && (!parent.defaultBranch || parent.head !== parent.defaultBranch)
  && repoOf(child.url) === repoOf(parent.url);

/** The open PR below this one. Both must be open, so a merged branch name cannot link anything. */
export const parentOf = (pr: PullRequestStatus, prs: PullRequestStatus[]): PullRequestStatus | undefined =>
  open(prs).find((p) => isStackedOn(pr, p));

export const childrenOf = (pr: PullRequestStatus, prs: PullRequestStatus[]): PullRequestStatus[] =>
  open(prs).filter((p) => isStackedOn(p, pr)).sort((a, b) => a.number - b.number);

/**
 * The chain the PR belongs to, or null when it is not stacked with another PR. Walks up to the root, then down through
 * the children; where a PR has several open children the chain follows the lowest number and `fork` is set.
 */
export function chainOf(pr: PullRequestStatus, prs: PullRequestStatus[]): Chain | null {
  if (pr.state !== "open") return null;
  let root = pr;
  const seen = new Set([pr.url]);
  for (let p = parentOf(root, prs); p && !seen.has(p.url); p = parentOf(root, prs)) {
    seen.add(p.url);
    root = p;
  }
  const chain = [root];
  let fork = false;
  const visited = new Set([root.url]);
  for (let cur = root; ;) {
    const kids = childrenOf(cur, prs).filter((k) => !visited.has(k.url));
    if (kids.length === 0) break;
    if (kids.length > 1) fork = true;
    cur = kids[0];
    visited.add(cur.url);
    chain.push(cur);
  }
  // A fork elsewhere in the component (a second child lower down) also counts.
  if (!fork) fork = chain.some((c) => childrenOf(c, prs).length > 1);
  return chain.length < 2 ? null : { prs: chain, rootBase: root.base ?? "", fork };
}

/** `main ← #11 ← #12 ← #13 (top)` */
export const chainLine = (c: Chain): string =>
  `${c.rootBase || "base"} ← ${c.prs.map((p) => `#${p.number}`).join(" ← ")} (top)`;

/** The PRs stacked above this one, from the top of the stack down: the order a safe merge goes in. */
export function aboveTopDown(pr: PullRequestStatus, prs: PullRequestStatus[]): PullRequestStatus[] {
  const levels: PullRequestStatus[][] = [];
  const seen = new Set([pr.url]);
  let frontier = childrenOf(pr, prs);
  while (frontier.length > 0) {
    const level = frontier.filter((p) => !seen.has(p.url));
    if (level.length === 0) break;
    level.forEach((p) => seen.add(p.url));
    levels.push(level);
    frontier = level.flatMap((p) => childrenOf(p, prs));
  }
  return levels.reverse().flat();
}

/** The merge-order warning shown instead of the plain merge confirm, or null when nothing is stacked on the PR. */
export function mergeOrderWarning(pr: PullRequestStatus, prs: PullRequestStatus[]): string | null {
  const kids = childrenOf(pr, prs);
  if (kids.length === 0) return null;
  const n = (p: PullRequestStatus) => `#${p.number}`;
  const order = `${aboveTopDown(pr, prs).map(n).join(", then ")}, then this`;
  return `${kids.length === 1 ? `${n(kids[0])} is` : `${kids.map(n).join(", ")} are`} stacked on this PR. ` +
    `Merge from the top of the stack down: merge ${order}. ` +
    `Merging this first leaves ${kids.map(n).join(", ")} targeting a merged branch.`;
}
