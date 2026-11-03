import { describe, expect, it } from "vitest";
import { nudgesFor, owningBuilder } from "./prNudges";
import { session } from "./test/fixtures";
import type { PullRequestStatus } from "./types";

const URL = "https://github.com/acme/widgets/pull/12";
const st = (o: Partial<PullRequestStatus> = {}): PullRequestStatus => ({
  url: URL, number: 12, title: "Fix it", state: "open", draft: false, checks: "passing", canMerge: true,
  base: "main", head: "fyrd/alpha", checksPassed: 1, checksFailed: 0, checksPending: 0, failingCheck: null,
  review: "none", mergeable: "mergeable", mergeState: "clean", ...o,
});
const ctl = session({ id: "c", title: "controller", role: "controller" });
const builder = session({ id: "b1", title: "builder-1", role: "builder", branch: "fyrd/alpha", pullRequests: [URL] });
const kinds = (n: ReturnType<typeof nudgesFor>) => n.map((x) => x.kind);

describe("nudgesFor", () => {
  it("an open ready PR offers validation, review and watch to the Controller", () => {
    const n = nudgesFor(st(), [ctl, builder], ctl);
    expect(kinds(n)).toEqual(["validate", "review", "watch"]);
    expect(n.every((x) => x.to?.id === "c")).toBe(true);
  });

  it("a draft has no Request review", () => {
    expect(kinds(nudgesFor(st({ draft: true }), [ctl, builder], ctl))).toEqual(["validate", "watch"]);
  });

  it.each(["merged", "closed", "unknown"] as const)("a %s PR has no nudges", (state) => {
    expect(nudgesFor(st({ state }), [ctl, builder], ctl)).toEqual([]);
  });

  it("the texts are exact", () => {
    const n = Object.fromEntries(nudgesFor(st(), [ctl, builder], ctl).map((x) => [x.kind, x.text]));
    expect(n.validate).toBe(`Validate PR ${URL}. Base branch: main. Builder: builder-1 (b1).`);
    expect(n.review).toBe(`PR #12 is ready: ${URL}`);
    expect(n.watch).toBe(`Watch ${URL} and fix anything that comes up`);
  });

  it("validation leaves the Builder out when no builder owns the PR", () => {
    const n = nudgesFor(st(), [ctl], ctl).find((x) => x.kind === "validate")!;
    expect(n.text).toBe(`Validate PR ${URL}. Base branch: main.`);
  });

  it("Watch PR asks first", () => {
    expect(nudgesFor(st(), [ctl], ctl).find((x) => x.kind === "watch")!.confirm).toBe("Start a PR Monitor for #12?");
    expect(nudgesFor(st(), [ctl], ctl).filter((x) => x.confirm)).toHaveLength(1);
  });

  it("the Controller items are disabled with 'No Controller' when there is none", () => {
    const n = nudgesFor(st(), [builder], null);
    expect(n.every((x) => x.disabledReason === "No Controller" && x.to === null)).toBe(true);
  });

  it("Fix failing checks goes to the owning Builder, naming the failing check", () => {
    const n = nudgesFor(st({ checks: "failing", failingCheck: { name: "lint", url: null } }), [ctl, builder], ctl);
    const fix = n.find((x) => x.kind === "fix")!;
    expect(fix.to?.id).toBe("b1");
    expect(fix.text).toBe(`Checks are failing on ${URL}: lint. Fix them, run the tests, push, and reply as before.`);
    expect(fix.disabledReason).toBeUndefined();
  });

  it("Fix failing checks covers conflicts, with the conflict text", () => {
    const fix = nudgesFor(st({ mergeable: "conflicting" }), [ctl, builder], ctl).find((x) => x.kind === "fix")!;
    expect(fix.text).toBe(`${URL} has merge conflicts with main. Merge main in (no force-push), fix, run the tests, push, and reply as before.`);
  });

  it("Fix failing checks names every failing check", () => {
    const fix = nudgesFor(st({ checks: "failing", failingCheck: { name: "lint", url: null },
      failingChecks: [{ name: "lint", url: null }, { name: "build", url: null }] }), [ctl, builder], ctl).find((x) => x.kind === "fix")!;
    expect(fix.text).toBe(`Checks are failing on ${URL}: lint, build. Fix them, run the tests, push, and reply as before.`);
  });

  it("an archived Builder is never the target", () => {
    const archived = session({ id: "b0", title: "builder-0", role: "builder", status: "archived", branch: "fyrd/alpha", pullRequests: [URL] });
    expect(owningBuilder(URL, "fyrd/alpha", [archived, builder])?.id).toBe("b1");
    expect(owningBuilder(URL, "fyrd/alpha", [archived])).toBeNull();
    expect(kinds(nudgesFor(st({ checks: "failing" }), [ctl, archived], ctl))).not.toContain("fix");
  });

  it("Fix failing checks is hidden when the checks pass, or when no Builder owns the PR", () => {
    expect(kinds(nudgesFor(st(), [ctl, builder], ctl))).not.toContain("fix");
    expect(kinds(nudgesFor(st({ checks: "failing" }), [ctl], ctl))).not.toContain("fix");
    const notBuilder = session({ id: "v", role: "validator", pullRequests: [URL] });
    expect(kinds(nudgesFor(st({ checks: "failing" }), [ctl, notBuilder], ctl))).not.toContain("fix");
  });

  it("Fix failing checks still works without a Controller", () => {
    const fix = nudgesFor(st({ checks: "failing" }), [builder], null).find((x) => x.kind === "fix")!;
    expect(fix.to?.id).toBe("b1");
  });
});

describe("owningBuilder", () => {
  it("prefers the builder whose branch is the PR's head", () => {
    const other = session({ id: "b2", role: "builder", branch: "fyrd/other", pullRequests: [URL] });
    expect(owningBuilder(URL, "fyrd/alpha", [other, builder])?.id).toBe("b1");
    expect(owningBuilder(URL, "fyrd/zzz", [other, builder])?.id).toBe("b2");
  });
  it("is null when no builder owns it", () => {
    expect(owningBuilder(URL, "fyrd/alpha", [session({ role: "builder", pullRequests: [] })])).toBeNull();
  });
});
