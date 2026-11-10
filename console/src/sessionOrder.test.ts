import { describe, expect, it } from "vitest";
import { dropGroup, dropSession, groupSessions, moveGroupBy, moveSessionBy, orderOf, personaLabel } from "./sessionOrder";
import type { HubSessionView } from "./types";

const s = (id: string, role: string, title = id): HubSessionView => ({ id, title, role, status: "idle", repo: null, branch: null, createdAt: null, lastActivity: null, tags: [], pullRequests: [] });
const all = [s("b2", "builder"), s("b1", "builder"), s("v1", "validator"), s("x", "")];
const ids = (g: ReturnType<typeof groupSessions>) => g.map((x) => `${x.persona}:${x.sessions.map((y) => y.id).join(",")}`);

describe("sessionOrder", () => {
  it("groups by persona, default alphabetical, with a blank role in Other", () => {
    expect(ids(groupSessions(all, { sessions: [], groups: [] }))).toEqual(["builder:b1,b2", "validator:v1", "other:x"]);
  });
  it("applies the saved order; unlisted sessions and personas go last", () => {
    const g = groupSessions([...all, s("b3", "builder")], { sessions: ["b2", "b1"], groups: ["validator"] });
    expect(ids(g)).toEqual(["validator:v1", "builder:b2,b1,b3", "other:x"]);
  });
  it("moves sessions and groups by one place, ignoring the ends", () => {
    const g = groupSessions(all, { sessions: [], groups: [] });
    expect(ids(moveSessionBy(g, "b2", -1))[0]).toBe("builder:b2,b1");
    expect(ids(moveSessionBy(g, "b1", -1))[0]).toBe("builder:b1,b2");
    expect(ids(moveGroupBy(g, "validator", -1))).toEqual(["builder:b1,b2", "validator:v1", "other:x"]);
  });
  it("drops within a group, across groups (moves the group) and groups on groups", () => {
    const g = groupSessions(all, { sessions: [], groups: [] });
    expect(ids(dropSession(g, "b2", "b1"))[0]).toBe("builder:b2,b1");
    expect(ids(dropSession(g, "v1", "b1"))).toEqual(["validator:v1", "builder:b1,b2", "other:x"]);
    expect(ids(dropGroup(g, "other", "builder"))[0]).toBe("other:x");
  });
  it("orderOf reproduces the display and keeps unlisted ids", () => {
    const g = moveSessionBy(groupSessions(all, { sessions: ["gone"], groups: ["old"] }), "b2", -1);
    const o = orderOf(g, { sessions: ["gone"], groups: ["old"] });
    expect(o.sessions).toEqual(["b2", "b1", "x", "v1", "gone"]);
    expect(o.groups).toEqual(["builder", "other", "validator", "old"]);
    expect(ids(groupSessions(all, o))[0]).toBe("builder:b2,b1");
  });
  it("labels personas", () => {
    expect(personaLabel("research-lead")).toBe("Research lead");
    expect(personaLabel("other")).toBe("Other");
  });
});
