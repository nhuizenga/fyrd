import { describe, expect, it } from "vitest";
import { confirmLine, jobFor, running, stepLine } from "./stackMerge";
import type { StackMergeView } from "./types";

const U = (n: number) => `https://github.com/acme/widgets/pull/${n}`;
const job = (o: Partial<StackMergeView> = {}): StackMergeView => ({
  id: "j1", repo: "acme/widgets", prs: [U(13), U(12), U(11)], index: 0, status: "waiting", message: null,
  startedAt: "2026-11-02T12:00:00Z", updatedAt: "2026-11-02T12:00:00Z", ...o,
});

describe("stepLine", () => {
  it("says what each PR is doing while the job waits on the second step", () => {
    const j = job({ index: 1, status: "waiting" });
    expect(stepLine(j, U(13))).toBe("Merged");
    expect(stepLine(j, U(12))).toBe("Waiting for checks on #12 (step 2 of 3)");
    expect(stepLine(j, U(11))).toBe("Queued (step 3 of 3)");
  });
  it("merging, done, failed and cancelled", () => {
    expect(stepLine(job({ index: 0, status: "merging" }), U(13))).toBe("Merging #13 (step 1 of 3)");
    expect(stepLine(job({ index: 3, status: "done" }), U(11))).toBe("Merged");
    const failed = job({ index: 1, status: "failed", message: "Checks are failing on #12." });
    expect(stepLine(failed, U(12))).toBe("Failed: Checks are failing on #12.");
    expect(stepLine(failed, U(13))).toBe("Merged");
    expect(stepLine(failed, U(11))).toBe("Not merged");
    expect(stepLine(job({ index: 1, status: "cancelled", message: "Cancelled." }), U(12))).toBe("Cancelled");
  });
});

describe("jobFor and running", () => {
  it("finds the job for a PR, preferring a running one", () => {
    const old = job({ id: "old", status: "failed", startedAt: "2026-11-02T10:00:00Z" });
    const live = job({ id: "live", status: "waiting", startedAt: "2026-11-02T09:00:00Z" });
    expect(jobFor(U(12), [old, live])?.id).toBe("live");
    expect(jobFor(U(99), [old, live])).toBeUndefined();
    expect(jobFor(U(12), undefined)).toBeUndefined();
    expect(running(old)).toBe(false);
    expect(running(live)).toBe(true);
  });
});

it("confirmLine lists the order, base and the squash-and-wait promise", () => {
  expect(confirmLine([13, 12, 11], "main")).toBe("#13 → #12 → #11 into main, squash, waiting for checks between steps");
});
