import { describe, expect, it } from "vitest";
import { aboveTopDown, chainLine, chainOf, childrenOf, mergeOrderWarning, parentOf } from "./stacks";
import type { PullRequestStatus } from "./types";

const pr = (number: number, head: string, base: string, state: PullRequestStatus["state"] = "open"): PullRequestStatus => ({
  url: `https://github.com/acme/widgets/pull/${number}`, number, title: `PR ${number}`, state, draft: false, checks: "passing",
  canMerge: true, head, base, checksPassed: 0, checksFailed: 0, checksPending: 0, failingCheck: null,
  review: "none", mergeable: "mergeable", mergeState: "clean",
});
const nums = (l: PullRequestStatus[]) => l.map((p) => p.number);

describe("stack links", () => {
  const prs = [pr(11, "a", "main"), pr(12, "b", "a"), pr(13, "c", "b")];
  it("B is stacked on A when B.base == A.head", () => {
    expect(parentOf(prs[1], prs)?.number).toBe(11);
    expect(parentOf(prs[0], prs)).toBeUndefined();
    expect(nums(childrenOf(prs[0], prs))).toEqual([12]);
  });
  it("merged and closed PRs are never linked", () => {
    const list = [pr(11, "a", "main", "merged"), pr(12, "b", "a")];
    expect(parentOf(list[1], list)).toBeUndefined();
    expect(chainOf(list[1], list)).toBeNull();
  });
});

describe("chainOf", () => {
  const prs = [pr(13, "c", "b"), pr(11, "a", "main"), pr(12, "b", "a")];

  it("orders the chain root to top, whichever PR you start from", () => {
    for (const p of prs) expect(nums(chainOf(p, prs)!.prs)).toEqual([11, 12, 13]);
  });
  it("knows the root's base and renders the chain line", () => {
    const c = chainOf(prs[0], prs)!;
    expect(c.rootBase).toBe("main");
    expect(c.fork).toBe(false);
    expect(chainLine(c)).toBe("main ← #11 ← #12 ← #13 (top)");
  });
  it("is null for a PR that is not stacked", () => {
    expect(chainOf(pr(20, "x", "main"), [...prs, pr(20, "x", "main")])).toBeNull();
  });
  it("detects a fork, at the root or lower down", () => {
    const atRoot = [pr(1, "a", "main"), pr(2, "b", "a"), pr(3, "c", "a")];
    expect(chainOf(atRoot[0], atRoot)!.fork).toBe(true);
    const lower = [pr(1, "a", "main"), pr(2, "b", "a"), pr(3, "c", "b"), pr(4, "d", "b")];
    expect(chainOf(lower[0], lower)!.fork).toBe(true);
    expect(chainOf(lower[3], lower)!.fork).toBe(true);
  });
  it("does not loop on a cycle", () => {
    const cyc = [pr(1, "a", "b"), pr(2, "b", "a")];
    expect(chainOf(cyc[0], cyc)).not.toBeUndefined();
  });
});

describe("merge order", () => {
  const prs = [pr(11, "a", "main"), pr(12, "b", "a"), pr(13, "c", "b")];
  it("lists what is above a PR from the top down", () => {
    expect(nums(aboveTopDown(prs[0], prs))).toEqual([13, 12]);
    expect(aboveTopDown(prs[2], prs)).toEqual([]);
  });
  it("warns with the exact text, naming the top-down order", () => {
    expect(mergeOrderWarning(prs[0], prs)).toBe(
      "#12 is stacked on this PR. Merge from the top of the stack down: merge #13, then #12, then this. Merging this first leaves #12 targeting a merged branch.",
    );
  });
  it("no warning for the top of a stack or an unstacked PR", () => {
    expect(mergeOrderWarning(prs[2], prs)).toBeNull();
    expect(mergeOrderWarning(pr(20, "x", "main"), prs)).toBeNull();
  });
  it("a fork names every child", () => {
    const f = [pr(1, "a", "main"), pr(2, "b", "a"), pr(3, "c", "a")];
    expect(mergeOrderWarning(f[0], f)).toMatch(/^#2, #3 are stacked on this PR\./);
  });
});
