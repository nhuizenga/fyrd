import type { HubSessionView } from "./types";

/** The owner's manual order: session ids (display order) and persona groups (display order). */
export interface SessionOrder {
  sessions: string[];
  groups: string[];
}

export const EMPTY_ORDER: SessionOrder = { sessions: [], groups: [] };
export const OTHER_PERSONA = "other";

export interface SessionGroup {
  /** The persona key (a `fyrd-role-*` value, or "other"). */
  persona: string;
  sessions: HubSessionView[];
}

/** "research-lead" -> "Research lead". */
export const personaLabel = (persona: string) => {
  const t = persona.replace(/-/g, " ").trim();
  return t ? t[0].toUpperCase() + t.slice(1) : "Other";
};

export const personaOf = (s: HubSessionView) => s.role?.trim() || OTHER_PERSONA;

const byName = (a: HubSessionView, b: HubSessionView) =>
  a.title.localeCompare(b.title, undefined, { numeric: true, sensitivity: "base" });

/**
 * Groups sessions by persona. Groups follow `order.groups` (unlisted personas go last, alphabetically); inside a group
 * sessions follow `order.sessions` (unlisted sessions go to the bottom of their group, by name).
 */
export function groupSessions(sessions: HubSessionView[], order: SessionOrder): SessionGroup[] {
  const rank = new Map(order.sessions.map((id, i) => [id, i]));
  const byPersona = new Map<string, HubSessionView[]>();
  for (const s of sessions) byPersona.set(personaOf(s), [...(byPersona.get(personaOf(s)) ?? []), s]);
  const groupRank = new Map(order.groups.map((g, i) => [g, i]));
  return [...byPersona.entries()]
    .map(([persona, list]) => ({
      persona,
      sessions: [...list].sort((a, b) => {
        const ra = rank.get(a.id), rb = rank.get(b.id);
        if (ra !== undefined && rb !== undefined) return ra - rb;
        if (ra !== undefined) return -1;
        if (rb !== undefined) return 1;
        return byName(a, b);
      }),
    }))
    .sort((a, b) => {
      const ra = groupRank.get(a.persona), rb = groupRank.get(b.persona);
      if (ra !== undefined && rb !== undefined) return ra - rb;
      if (ra !== undefined) return -1;
      if (rb !== undefined) return 1;
      return a.persona.localeCompare(b.persona);
    });
}

/** The order that reproduces exactly what `groups` shows (so a reorder keeps everything else where it is). */
export const orderOf = (groups: SessionGroup[], previous: SessionOrder = EMPTY_ORDER): SessionOrder => {
  const shown = new Set(groups.flatMap((g) => g.sessions.map((s) => s.id)));
  // Keep ids of sessions that are not listed right now (filtered out or gone) so their place survives.
  const kept = previous.sessions.filter((id) => !shown.has(id));
  return {
    sessions: [...groups.flatMap((g) => g.sessions.map((s) => s.id)), ...kept],
    groups: [...groups.map((g) => g.persona), ...previous.groups.filter((p) => !groups.some((g) => g.persona === p))],
  };
};

function move<T>(list: T[], from: number, to: number): T[] {
  if (from < 0 || to < 0 || from >= list.length || to >= list.length || from === to) return list;
  const next = [...list];
  next.splice(to, 0, next.splice(from, 1)[0]);
  return next;
}

/** Moves a session `delta` places inside its group (-1 up, +1 down). */
export function moveSessionBy(groups: SessionGroup[], id: string, delta: number): SessionGroup[] {
  return groups.map((g) => {
    const i = g.sessions.findIndex((s) => s.id === id);
    return i < 0 ? g : { ...g, sessions: move(g.sessions, i, i + delta) };
  });
}

/** Moves a persona group `delta` places (-1 up, +1 down). */
export function moveGroupBy(groups: SessionGroup[], persona: string, delta: number): SessionGroup[] {
  const i = groups.findIndex((g) => g.persona === persona);
  return move(groups, i, i + delta);
}

/**
 * Drops session `id` on session `targetId`. In the same group it takes the target's place. In another group a session
 * cannot change persona, so the session's whole group moves to sit where the target's group is.
 */
export function dropSession(groups: SessionGroup[], id: string, targetId: string): SessionGroup[] {
  const from = groups.find((g) => g.sessions.some((s) => s.id === id));
  const to = groups.find((g) => g.sessions.some((s) => s.id === targetId));
  if (!from || !to || id === targetId) return groups;
  if (from === to) {
    return groups.map((g) => g !== from ? g : { ...g, sessions: move(g.sessions, g.sessions.findIndex((s) => s.id === id), g.sessions.findIndex((s) => s.id === targetId)) });
  }
  return move(groups, groups.indexOf(from), groups.indexOf(to));
}

/** Drops persona group `persona` on group `targetPersona`. */
export function dropGroup(groups: SessionGroup[], persona: string, targetPersona: string): SessionGroup[] {
  return move(groups, groups.findIndex((g) => g.persona === persona), groups.findIndex((g) => g.persona === targetPersona));
}
