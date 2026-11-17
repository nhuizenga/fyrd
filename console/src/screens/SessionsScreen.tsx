import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Alert, Button, Chip, ProgressBar, SearchField, Text, TextField } from "@servicetitan/anvil2";
import TrashIcon from "@servicetitan/anvil2/assets/icons/material/round/delete_outline.svg?react";
import EditIcon from "@servicetitan/anvil2/assets/icons/material/round/edit.svg?react";
import DownloadIcon from "@servicetitan/anvil2/assets/icons/material/round/download.svg?react";
import OpenIcon from "@servicetitan/anvil2/assets/icons/material/round/open_in_new.svg?react";
import RobotIcon from "@servicetitan/anvil2/assets/icons/material/round/smart_toy.svg?react";
import ReplaceIcon from "@servicetitan/anvil2/assets/icons/material/round/swap_horiz.svg?react";
import UpIcon from "@servicetitan/anvil2/assets/icons/material/round/keyboard_arrow_up.svg?react";
import DownIcon from "@servicetitan/anvil2/assets/icons/material/round/keyboard_arrow_down.svg?react";
import DragIcon from "@servicetitan/anvil2/assets/icons/material/round/drag_indicator.svg?react";
import * as api from "../api";
import ColumnDivider from "../components/ColumnDivider";
import AddRepo from "../components/AddRepo";
import ScrollIntoView from "../components/ScrollIntoView";
import PullRequestItem, { prLabel } from "../components/PullRequestItem";
import { confirmLine, jobFor, running, stepLine } from "../stackMerge";
import { chainLine, chainOf, mergeOrderWarning, type Chain } from "../stacks";
import { nudgesFor, type Nudge } from "../prNudges";
import { HEALTH_LABEL, anyPending, healthOf } from "../prStatus";
import PrStatusIcon from "../components/PrStatusIcon";
import Composer, { DRAFT_KEY, readStored, writeStored } from "../components/Composer";
import Thread from "../components/Thread";
import StatusIcon from "../components/StatusIcon";
import type { HubCostView, HubSessionView, PullRequestStatus } from "../types";
import { lastSession, navigate, rememberSession, replaceRoute } from "../route";
import { fullTime, relativeTime } from "../time";
import { useTranscript } from "../useTranscript";
import { EMPTY_ORDER, dropGroup, dropSession, groupSessions, moveGroupBy, moveSessionBy, orderOf, personaLabel, type SessionOrder } from "../sessionOrder";
import type { ScreenProps } from "./ScreenProps";

const COMPOSER_KEY = "fyrd.composerHeight";
const COMPOSER_MIN = 72;
const COMPOSER_MAX = 480;
const COMPOSER_DEFAULT = 96;
const LIST_KEY = "fyrd.listWidth";
const PR_REFRESH_MS = 30_000;
const ATTRS_KEY = "fyrd.attrsWidth";
const SIDE_MIN = 200;
const SIDE_MAX = 560;
const SIDE_DEFAULT = 280;
const COLLAPSED_KEY = "fyrd.collapsedPersonas";

const readCollapsed = (): string[] => {
  try {
    const v = JSON.parse(readStored(COLLAPSED_KEY) ?? "[]");
    return Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : [];
  } catch {
    return [];
  }
};

const repoName = (repo: string) => repo.split("/").pop() || repo;

const usd = (n: number) => `$${n.toFixed(2)}`;
const pct = (ratio: number) => Math.round(ratio * 100);

/** The session's spend today, a bar against its daily limit, and the +$ raises. */
function CostBlock({ cost, raises }: { cost: HubCostView | null | undefined; raises?: ReactNode }) {
  if (!cost) return <><Text>—</Text>{raises}</>;
  return (
    <>
      <Text>{usd(cost.costToday)}{cost.estimated ? " (estimated)" : ""}</Text>
      <div className="cost-meter">
        <div className="cost-meter-bar">
          <ProgressBar
            aria-label={`${pct(cost.ratio)} percent of today's ${usd(cost.limitUsd)} limit used`}
            value={Math.min(100, cost.ratio * 100)}
            max={100}
            error={cost.blocked !== null}
          />
        </div>
        {raises}
      </div>
      <Text size="small" subdued>
        {cost.exempt ? "Controller · limit not enforced" : `${pct(cost.ratio)}% of ${usd(cost.limitUsd)} limit today`}
      </Text>
    </>
  );
}

/** How close to the bottom (px) still counts as "following" the thread. */
const FOLLOW_SLACK = 48;

export default function SessionsScreen({ state, onChanged, routeId }: ScreenProps) {
  const [search, setSearch] = useState("");
  const selectedId = routeId ?? null;
  const [composerHeight, setComposerHeight] = useState(() => {
    const n = Number(readStored(COMPOSER_KEY));
    return n >= COMPOSER_MIN && n <= COMPOSER_MAX ? n : COMPOSER_DEFAULT;
  });
  const resizeComposer = (h: number) => {
    const next = Math.min(COMPOSER_MAX, Math.max(COMPOSER_MIN, Math.round(h)));
    setComposerHeight(next);
    writeStored(COMPOSER_KEY, String(next));
  };
  // Sessions with a message in flight (the box is read-only meanwhile), and messages shown optimistically until the transcript echoes them.
  const [sendingIds, setSendingIds] = useState<string[]>([]);
  const [outbox, setOutbox] = useState<{ id: string; sessionId: string; text: string; seqAtSend: number; sentAt: number; acked: boolean }[]>([]);
  const outboxSeq = useRef(0);
  const storedWidth = (key: string) => {
    const n = Number(readStored(key));
    return n >= SIDE_MIN && n <= SIDE_MAX ? n : SIDE_DEFAULT;
  };
  const [listWidth, setListWidth] = useState(() => storedWidth(LIST_KEY));
  const [attrsWidth, setAttrsWidth] = useState(() => storedWidth(ATTRS_KEY));
  const resizeList = (w: number) => { setListWidth(w); writeStored(LIST_KEY, String(w)); };
  const resizeAttrs = (w: number) => { setAttrsWidth(w); writeStored(ATTRS_KEY, String(w)); };
  const [error, setError] = useState<string | null>(null);
  const [createWarning, setCreateWarning] = useState<string | null>(null);
  const [pausedIds, setPausedIds] = useState<string[]>([]);
  const [confirmReplace, setConfirmReplace] = useState(false);
  const [prStatuses, setPrStatuses] = useState<PullRequestStatus[]>([]);
  const nudgesInFlight = useRef(new Set<string>());
  const [prNotices, setPrNotices] = useState<Record<string, string | null>>({});
  const [confirmStack, setConfirmStack] = useState<{ url: string; top: number[]; rootBase: string } | null>(null);
  const [confirmNudge, setConfirmNudge] = useState<{ url: string; nudge: Nudge } | null>(null);
  const [prErrors, setPrErrors] = useState<Record<string, string | null>>({});
  const [confirmMerge, setConfirmMerge] = useState<PullRequestStatus | null>(null);
  const [exporting, setExporting] = useState<string[]>([]);
  const [confirmArchive, setConfirmArchive] = useState<HubSessionView | null>(null);
  const [renaming, setRenaming] = useState<{ session: HubSessionView; title: string } | null>(null);
  const [raising, setRaising] = useState(false);
  // A raise's response, shown until the poll catches up (the server only requests a poll, so the next state can be stale).
  const [raised, setRaised] = useState<{ id: string; from: number; view: HubCostView } | null>(null);
  const [creating, setCreating] = useState(false);
  const [order, setOrder] = useState<SessionOrder>(EMPTY_ORDER);
  const [collapsed, setCollapsed] = useState<string[]>(readCollapsed);
  const [dragging, setDragging] = useState<{ kind: "session" | "group"; id: string } | null>(null);
  useEffect(() => {
    void (async () => {
      try {
        const o = await api.getSessionOrder();
        if (o) setOrder({ sessions: o.sessions ?? [], groups: o.groups ?? [] });
      } catch { /* the default order is fine */ }
    })();
  }, []);
  const toggleGroup = (persona: string) => setCollapsed((c) => {
    const next = c.includes(persona) ? c.filter((p) => p !== persona) : [...c, persona];
    writeStored(COLLAPSED_KEY, JSON.stringify(next));
    return next;
  });
  const transcript = useTranscript(selectedId);
  // Optimistic messages show until the transcript has their echo: a newer user item (seq above the newest one loaded
  // at send time) with the same whitespace-normalised text, or, once the server has accepted the message, any such
  // item at all (the server may wrap or rewrite the text). Each item answers one message only. When nothing was
  // loaded at send time (the replay hasn't landed), older history can't be told apart by seq, so the item's own
  // timestamp must also be recent.
  const outgoing = useMemo(() => {
    const norm = (t: string) => t.replace(/\s+/g, " ").trim();
    const taken = new Set<string>();
    return outbox.filter((m) => {
      if (m.sessionId !== selectedId) return false;
      const fresh = transcript.items.filter((i) => i.seq > m.seqAtSend && i.role === "user" && i.kind !== "question" && !taken.has(i.key)
        && (m.seqAtSend >= 0 || !(Date.parse(i.at) < m.sentAt - 60_000)));
      const echo = fresh.find((i) => norm(i.text) === norm(m.text)) ?? (m.acked ? fresh[0] : undefined);
      if (echo) taken.add(echo.key);
      return !echo;
    });
  }, [outbox, selectedId, transcript.items]);

  const logRef = useRef<HTMLDivElement>(null);
  const itemsRef = useRef<HTMLDivElement>(null);
  const following = useRef(true);
  const autoTop = useRef(0);

  const controllerId = state?.controller?.sessionId ?? null;
  // The Controller is created through the owner's CLI, so it carries no Fyrd tags: identify it by id.
  const isController = (s: HubSessionView) => s.id === controllerId;
  const all = state?.sessions ?? [];
  const controller = all.find(isController) ?? null;
  // Grouped by role, then by name (numeric-aware, so builder-2 comes before builder-10).
  const byRoleThenName = (a: HubSessionView, b: HubSessionView) =>
    a.role.localeCompare(b.role, undefined, { sensitivity: "base" }) ||
    a.title.localeCompare(b.title, undefined, { numeric: true, sensitivity: "base" });
  const others = all.filter((s) => !isController(s)).sort(byRoleThenName);
  const q = search.trim().toLowerCase();
  const sessions = q ? others.filter((s) => s.title.toLowerCase().includes(q)) : others;
  const groups = groupSessions(sessions, order);
  // Reordering works on what is shown, so it is off while a search hides sessions.
  const canReorder = q === "";
  const applyGroups = (next: typeof groups) => {
    if (next === groups) return;
    const saved = orderOf(next, order);
    setOrder(saved);
    void (async () => {
      try { await api.saveSessionOrder(saved); } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    })();
  };
  const selected = all.find((s) => s.id === selectedId) ?? null;
  const hasThread = selected !== null;
  // No token, or a bad one: there are no PR statuses or actions, only a note saying why.
  const gitHubNote = state?.gitHub
    ? !state.gitHub.configured ? "Set GH_TOKEN before /fyrd:up to see PR status and act on PRs." : state.gitHub.error
    : null;
  const prKey = selected ? `${selected.id}|${selected.pullRequests.join(",")}` : "";

  const loadPrs = useCallback(async () => {
    if (!selected || selected.pullRequests.length === 0) return setPrStatuses([]);
    try {
      setPrStatuses((await api.pullRequests(selected.id)) ?? []);
    } catch {
      setPrStatuses([]); // statuses are a nicety; the plain PR links still show
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [prKey]);
  useEffect(() => {
    setConfirmMerge(null);
    setConfirmNudge(null);
    setConfirmStack(null);
    void loadPrs();
  }, [loadPrs]);

  // A PR's status as the last hub poll saw it; the details pane also re-reads live.
  const listed = (url: string) => state?.pullRequests?.[url];

  const sessionPrs = (selected?.pullRequests ?? [])
    .map((u) => prStatuses.find((p) => p.url === u) ?? listed(u))
    .filter((p): p is PullRequestStatus => p !== undefined);
  const chains = sessionPrs.reduce<Chain[]>((acc, p) => {
    const c = chainOf(p, sessionPrs);
    return c && !acc.some((x) => x.prs[0].url === c.prs[0].url) ? [...acc, c] : acc;
  }, []);

  // Merge stack: offered on a PR in a linear chain of two or more open PRs, unless a job already runs on it.
  const stackProps = (u: string) => {
    const st = sessionPrs.find((p) => p.url === u);
    const job = jobFor(u, state?.stackMerges);
    const chain = st ? chainOf(st, sessionPrs) : null;
    const busy = job !== undefined && running(job);
    const atStep = job !== undefined && running(job) && job.prs[job.index] === u;
    return {
      canMergeStack: !!chain && !chain.fork && !busy,
      onMergeStack: () => chain && setConfirmStack({ url: u, top: [...chain.prs].reverse().map((p) => p.number), rootBase: chain.rootBase }),
      jobLine: job ? stepLine(job, u) : null,
      jobFailed: job?.status === "failed" && job.prs[job.index] === u,
      onCancelJob: atStep && job ? () => void cancelJob(job.id, u) : undefined,
    };
  };

  // While a selected session has a PR with running checks, re-read it every 30 s so Merge appears soon after CI goes green.
  const pendingNow = selected
    ? anyPending(selected.pullRequests.map((u) => prStatuses.find((p) => p.url === u) ?? listed(u)))
    : false;
  useEffect(() => {
    if (!pendingNow) return;
    const t = setInterval(() => void loadPrs(), PR_REFRESH_MS);
    return () => clearInterval(t);
  }, [pendingNow, loadPrs]);

  const run = async (fn: () => Promise<unknown>) => {
    setError(null);
    try {
      await fn();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  // `#/sessions` with no id reopens the session viewed last, once the list shows it still exists.
  useEffect(() => {
    if (selectedId) {
      rememberSession(selectedId);
      return;
    }
    const last = lastSession();
    if (last && state?.sessions.some((x) => x.id === last)) replaceRoute("sessions", last);
  }, [selectedId, state?.sessions]);

  // Once the server reports the session as idle/archived, it is authoritative again.
  useEffect(() => {
    setPausedIds((ids) => ids.filter((id) => {
      const st = state?.sessions.find((x) => x.id === id)?.status;
      return st !== "idle" && st !== "archived";
    }));
  }, [state?.sessions]);

  useEffect(() => {
    following.current = true;
    autoTop.current = 0;
  }, [selectedId]);

  // Keep the newest message in view unless the owner has scrolled up to read.
  const stickToBottom = useCallback(() => {
    const el = logRef.current;
    if (!el || !following.current) return;
    el.scrollTop = el.scrollHeight;
    autoTop.current = el.scrollTop;
  }, []);

  useLayoutEffect(stickToBottom, [stickToBottom, transcript.items.length + transcript.hiddenCount, outgoing.length, selectedId, hasThread]);

  // Content keeps growing after a render (fonts, markdown tables, a deep link's thread appearing late): keep following it.
  useEffect(() => {
    const inner = itemsRef.current;
    if (!inner) return;
    const observer = new ResizeObserver(stickToBottom);
    observer.observe(inner);
    return () => observer.disconnect();
  }, [stickToBottom, hasThread]);

  // Only the owner scrolling up stops following: the scroll events from our own jumps arrive late, after more
  // items have landed, and would otherwise look like "not at the bottom".
  const onLogScroll = () => {
    const el = logRef.current;
    if (!el) return;
    if (el.scrollHeight - el.scrollTop - el.clientHeight <= FOLLOW_SLACK) following.current = true;
    else if (el.scrollTop < autoTop.current - 2) following.current = false;
  };

  // Always ask the server: it checks the stored Controller is still live, so an archived id never becomes a dead link.
  const openController = () =>
    run(async () => {
      try {
        const info = await api.openController();
        window.open(info.url, "_blank", "noopener");
      } catch (e) {
        // Gone since the last poll: refreshing brings back the Create Controller panel.
        if ((e as { status?: number }).status !== 404) throw e;
      }
      await onChanged();
    });

  // Runs the owner's claude CLI on the server, which can take a while: the button stays busy until it returns.
  const createController = () =>
    run(async () => {
      setCreating(true);
      setCreateWarning(null);
      try {
        const info = await api.createController();
        if (info.warning) setCreateWarning(info.warning);
      } finally {
        setCreating(false);
      }
      await onChanged();
    });

  const archiveSession = (s: HubSessionView) =>
    run(async () => {
      setConfirmArchive(null);
      await api.archiveSession(s.id);
      if (s.id === selectedId) navigate("sessions");
      await onChanged();
    });

  const renameSession = () =>
    run(async () => {
      if (!renaming) return;
      const title = renaming.title.trim();
      if (title === "" || title === renaming.session.title) return setRenaming(null);
      // The form stays open if the call fails, so the typed name isn't lost.
      await api.renameSession(renaming.session.id, title);
      setRenaming(null);
      await onChanged();
    });

  // A failed ready or merge is shown under its own PR, not only in the page-level alert.
  const prAction = async (url: string, fn: () => Promise<unknown>) => {
    setPrErrors((e) => ({ ...e, [url]: null }));
    try {
      await fn();
      await loadPrs();
      await onChanged();
    } catch (e) {
      setPrErrors((errs) => ({ ...errs, [url]: e instanceof Error ? e.message : String(e) }));
    }
  };

  // A nudge is an ordinary owner message to the Controller or the owning Builder, so sessions read it as the owner's.
  const sendNudge = async (url: string, nudge: Nudge) => {
    if (!nudge.to) return;
    // One send at a time per PR and kind: a rapid double click must not message the session twice.
    const key = `${url}|${nudge.kind}`;
    if (nudgesInFlight.current.has(key)) return;
    nudgesInFlight.current.add(key);
    setConfirmNudge(null);
    setPrErrors((e) => ({ ...e, [url]: null }));
    setPrNotices((n) => ({ ...n, [url]: null }));
    try {
      await api.sendMessage(nudge.to.id, nudge.text);
      setPrNotices((n) => ({ ...n, [url]: `Sent to ${nudge.to!.title}` }));
      await onChanged();
    } catch (e) {
      setPrErrors((errs) => ({ ...errs, [url]: e instanceof Error ? e.message : String(e) }));
    } finally {
      nudgesInFlight.current.delete(key);
    }
  };
  const startStack = async (url: string) => {
    setConfirmStack(null);
    setPrErrors((e) => ({ ...e, [url]: null }));
    try {
      await api.mergeStack(selected!.id, url);
      await onChanged();
    } catch (e) {
      setPrErrors((errs) => ({ ...errs, [url]: e instanceof Error ? e.message : String(e) }));
    }
  };
  const cancelJob = async (jobId: string, url: string) => {
    try {
      await api.cancelStackMerge(jobId);
      await onChanged();
    } catch (e) {
      setPrErrors((errs) => ({ ...errs, [url]: e instanceof Error ? e.message : String(e) }));
    }
  };
  const pickNudge = (url: string, nudge: Nudge) => (nudge.confirm ? setConfirmNudge({ url, nudge }) : void sendNudge(url, nudge));

  const replaceController = () =>
    run(async () => {
      setConfirmReplace(false);
      await api.retireController();
      await onChanged();
    });

  const cost = selected && raised?.id === selected.id && selected.cost?.limitUsd === raised.from ? raised.view : selected?.cost;
  const blocked = cost?.blocked ?? null;
  const sending = selected ? sendingIds.includes(selected.id) : false;
  // The newest loaded item's seq (not an index: the thread shows only a window of the items, which shifts as new ones arrive).
  const latestSeq = useRef(-1);
  latestSeq.current = transcript.items.reduce((m, i) => Math.max(m, i.seq), -1);
  const send = async (text: string, fileIds: string[] = [], fileNames: string[] = []): Promise<boolean> => {
    if (!selected) return false;
    const sentTo = selected.id;
    const shown = [text, ...fileNames.map((n) => `📎 ${n}`)].filter(Boolean).join("\n");
    const entry = { id: `out-${(outboxSeq.current += 1)}`, sessionId: sentTo, text: shown, seqAtSend: latestSeq.current, sentAt: Date.now(), acked: false };
    following.current = true;
    setSendingIds((ids) => [...ids, sentTo]);
    setOutbox((o) => [...o, entry]);
    setError(null);
    const drop = () => setOutbox((o) => o.filter((x) => x.id !== entry.id));
    try {
      await (fileIds.length ? api.sendMessage(sentTo, text, fileIds) : api.sendMessage(sentTo, text));
      // The server has the message: the next user item replaces the bubble whatever its text. Backstop in case no echo ever arrives.
      setOutbox((o) => o.map((x) => (x.id === entry.id ? { ...x, acked: true } : x)));
      setTimeout(drop, 15_000);
      return true;
    } catch (e) {
      drop();
      // Keep the owner's words: put them back unless they've already started something new in that box.
      if ((readStored(DRAFT_KEY(sentTo)) ?? "") === "") writeStored(DRAFT_KEY(sentTo), text);
      setError(e instanceof Error ? e.message : String(e));
      return false;
    } finally {
      setSendingIds((ids) => ids.filter((id) => id !== sentTo));
    }
  };

  const allQuestions = state?.questions;
  const pending = useMemo(
    () => (selected ? (allQuestions ?? []).filter((x) => x.sessionId === selected.id) : []),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [allQuestions, selected?.id],
  );
  const canPause = !!selected && !pausedIds.includes(selected.id) && (selected.status === "running" || selected.status === "requires_action");
  const canResume = !!selected && (pausedIds.includes(selected.id) || selected.status === "idle");

  const budget = state?.budget ?? null;
  const addBudget = (id: string, from: number, amount: 25 | 100) => {
    if (raising) return;
    setRaising(true);
    void run(async () => {
      try {
        const view = await api.addBudget(id, amount);
        if (view) setRaised({ id, from, view });
        await onChanged();
      } finally {
        setRaising(false);
      }
    });
  };

  /** Fetches the export first so a 404 or 502 shows in the console, then saves the blob as a file. */
  const exportSession = (s: HubSessionView) =>
    run(async () => {
      setExporting((ids) => [...ids, s.id]);
      try {
        const res = await fetch(api.exportUrl(s.id));
        if (!res.ok) {
          let detail: string | undefined;
          try { detail = (await res.json())?.detail; } catch { /* non-JSON error body */ }
          throw new Error(detail ?? `Export failed (${res.status})`);
        }
        const blob = await res.blob();
        const name = /filename="([^"]+)"/.exec(res.headers.get("Content-Disposition") ?? "")?.[1] ?? `${s.id}.jsonl`;
        const a = document.createElement("a");
        a.href = URL.createObjectURL(blob);
        a.download = name;
        a.click();
        URL.revokeObjectURL(a.href);
      } finally {
        setExporting((ids) => ids.filter((id) => id !== s.id));
      }
    });

  const exportButton = (s: HubSessionView) => (
    <Button
      appearance="ghost" size="small" icon={DownloadIcon} aria-label={`Export ${s.title}`} title="Export raw events (JSONL)"
      disabled={exporting.includes(s.id)} aria-busy={exporting.includes(s.id)} onClick={() => exportSession(s)}
    />
  );

  // What each session is doing right now, kept by the server so it shows for sessions that are not selected.
  const liveLine = (id: string) => {
    const p = state?.progress?.[id];
    if (!p) return null;
    const text = p.status || p.text.trim().split("\n").filter(Boolean).pop() || "";
    return text || null;
  };

  // True when any of these PRs is part of a chain (a session in a stack owns the whole stack).
  const anyStacked = (statuses: (PullRequestStatus | undefined)[]) => {
    const everything = Object.values(state?.pullRequests ?? {});
    return statuses.some((st) => st && chainOf(st, everything));
  };

  // One chip per session: {status icon} {stack icon} {number of PRs}. The status is the worst of the PRs.
  const prChip = (s: HubSessionView) => {
    if (s.pullRequests.length === 0) return null;
    const statuses = s.pullRequests.map(listed);
    const merging = s.pullRequests.some((u) => (state?.stackMerges ?? []).some((j) => running(j) && j.prs.includes(u)));
    const stacked = anyStacked(statuses);
    const tip = s.pullRequests.map((u, i) => {
      const st = statuses[i];
      if (!st) return prLabel(u);
      const health = healthOf(st);
      return `${prLabel(u)} ${st.title ? `${st.title} ` : ""}(${st.draft ? "draft" : st.state}${health !== "none" ? `, ${HEALTH_LABEL[health].toLowerCase()}` : ""})`;
    }).join(", ");
    return (
      <span className="pr-chip" role="group" title={tip} aria-label={`${s.pullRequests.length} pull request${s.pullRequests.length === 1 ? "" : "s"}`}>
        <PrStatusIcon statuses={statuses} />
        {stacked && <span className="pr-stack" role="img" aria-label="Stacked PR" title="Part of a stack">⇅</span>}
        {merging && <span className="pr-spinner" role="img" aria-label="Merging stack" title="A stack merge is running">⟳</span>}
        <span className="pr-chip-count">{s.pullRequests.length}</span>
      </span>
    );
  };

  const workerCount = (s: HubSessionView) => Math.max(state?.progress?.[s.id]?.workers?.length ?? 0, s.workersRunning ? 1 : 0);

  const row = (s: HubSessionView, actions?: ReactNode, drag?: { onDrop: () => void; onStart: () => void; onEnd: () => void }) => (
    <li
      key={s.id} className="session-item"
      draggable={!!drag} data-session-id={s.id}
      onDragStart={drag ? (e) => { e.dataTransfer.effectAllowed = "move"; drag.onStart(); } : undefined}
      onDragEnd={drag?.onEnd}
      onDragOver={drag ? (e) => { if (dragging?.kind === "session") e.preventDefault(); } : undefined}
      onDrop={drag ? (e) => { e.preventDefault(); drag.onDrop(); } : undefined}
    >
      <button type="button" className="row-btn session-row" aria-current={s.id === selectedId} onClick={() => navigate("sessions", s.id)}>
        <StatusIcon status={s.status} />
        <span className="session-row-text">
          <span className="session-row-title">{s.title}</span>
          {liveLine(s.id) && <span className="session-row-progress" title={liveLine(s.id) ?? undefined}>{liveLine(s.id)}</span>}
          {(s.repo || s.pullRequests.length > 0 || workerCount(s) > 0) && (
            <span className="session-row-chips">
              {workerCount(s) > 0 && (
                <Chip
                  icon={RobotIcon} label={String(workerCount(s))} size="small" className="chip-workers"
                  aria-label={`${workerCount(s)} worker${workerCount(s) === 1 ? "" : "s"} running`}
                  title={`${workerCount(s)} background worker${workerCount(s) === 1 ? "" : "s"} running for this session`}
                />
              )}
              {s.repo && <Chip label={repoName(s.repo)} size="small" title={s.repo} />}
              {prChip(s)}
            </span>
          )}
        </span>
      </button>
      {actions && <span className="session-item-actions">{actions}</span>}
    </li>
  );

  return (
    <div className="sessions">
      <div className="sessions-top">
        <div className="sessions-title">
          <Text variant="headline" el="h2" size="large">Sessions</Text>
          <Text subdued>{all.length} Claude Code sessions</Text>
          {budget && <Text subdued>Fyrd today: {usd(budget.fyrdCostToday)} of {usd(budget.fyrdLimitUsd)}</Text>}
        </div>
        {error && <Alert status="danger" title={error} role="alert" />}
        {createWarning && <Alert status="warning" title={createWarning} role="status" onClose={() => setCreateWarning(null)} />}
      </div>
      <div className="sessions-grid" style={{ "--list-w": `${listWidth}px`, "--attrs-w": `${attrsWidth}px` } as React.CSSProperties}>
        <aside className="pane pane-list" aria-label="Session list">
          {controller && (
            <ul className="session-list">
              {row(controller, (
                <>
                  {exportButton(controller)}
                  <Button appearance="ghost" size="small" icon={OpenIcon} aria-label="Open Controller" title="Open Controller in claude.ai" onClick={openController} />
                  <Button appearance="ghost" size="small" icon={ReplaceIcon} aria-label="Replace Controller" title="Replace Controller" onClick={() => setConfirmReplace(true)} />
                </>
              ))}
            </ul>
          )}
          {confirmReplace && (
            <Alert status="warning" title="Replace the Controller?">
              <Text>This archives the current Controller. You'll then create a new one.</Text>
              <div style={{ display: "flex", gap: 8 }}>
                <Button appearance="primary" size="small" onClick={replaceController}>Archive and replace</Button>
                <Button appearance="ghost" size="small" onClick={() => setConfirmReplace(false)}>Cancel</Button>
              </div>
            </Alert>
          )}
          {state && !state.controller && (
            <section className="create-controller" aria-labelledby="create-controller-h">
              <Text variant="headline" el="h3" size="small" id="create-controller-h">Create Controller</Text>
              <Text size="small">
                Fyrd starts a new claude.ai session with your claude CLI and sends it the Controller's instructions.
              </Text>
              <div>
                <Button appearance="primary" size="small" disabled={creating} aria-busy={creating} onClick={createController}>
                  {creating ? "Creating…" : "Create Controller"}
                </Button>
              </div>
            </section>
          )}
          <SearchField aria-label="Search sessions" placeholder="Search sessions" value={search} onChange={(e) => setSearch(e.target.value)} onClear={() => setSearch("")} />
          {sessions.length === 0 && <Text subdued size="small">{q ? "No sessions match that search." : "No other sessions."}</Text>}
          {confirmArchive && (
            <Alert status="warning" title={`Archive ${confirmArchive.title}?`}>
              <Text>This archives the session on claude.ai and removes it from this list.</Text>
              <div style={{ display: "flex", gap: 8 }}>
                <Button appearance="primary" size="small" onClick={() => archiveSession(confirmArchive)}>Archive session</Button>
                <Button appearance="ghost" size="small" onClick={() => setConfirmArchive(null)}>Cancel</Button>
              </div>
            </Alert>
          )}
          {renaming && (
            <form
              className="rename-form"
              aria-label={`Rename ${renaming.session.title}`}
              onSubmit={(e) => { e.preventDefault(); void renameSession(); }}
            >
              <TextField
                label="Session name"
                autoFocus
                value={renaming.title}
                onChange={(e) => setRenaming({ ...renaming, title: e.target.value })}
                onKeyDown={(e) => { if (e.key === "Escape") setRenaming(null); }}
              />
              <div style={{ display: "flex", gap: 8 }}>
                <Button type="submit" appearance="primary" size="small" disabled={renaming.title.trim() === ""}>Save name</Button>
                <Button appearance="ghost" size="small" onClick={() => setRenaming(null)}>Cancel</Button>
              </div>
            </form>
          )}
          <div className="session-groups pane-scroll">
            {groups.map((g, gi) => {
              const open = !collapsed.includes(g.persona);
              const name = personaLabel(g.persona);
              return (
                <section
                  key={g.persona} className="session-group" aria-label={`${name} sessions`} data-persona={g.persona}
                  onDragOver={(e) => { if (dragging?.kind === "group") e.preventDefault(); }}
                  onDrop={(e) => {
                    if (dragging?.kind !== "group") return;
                    e.preventDefault();
                    applyGroups(dropGroup(groups, dragging.id, g.persona));
                    setDragging(null);
                  }}
                >
                  <div className="session-group-head">
                    {canReorder && (
                      <span
                        className="drag-handle" draggable aria-hidden="true" title={`Drag to move ${name}`}
                        onDragStart={(e) => { e.dataTransfer.effectAllowed = "move"; setDragging({ kind: "group", id: g.persona }); }}
                        onDragEnd={() => setDragging(null)}
                      ><DragIcon /></span>
                    )}
                    <button type="button" className="session-group-toggle" aria-expanded={open} onClick={() => toggleGroup(g.persona)}>
                      <span aria-hidden="true">{open ? "▾" : "▸"}</span>
                      <span className="session-group-name">{name}</span>
                      <Chip label={String(g.sessions.length)} size="small" aria-label={`${g.sessions.length} session${g.sessions.length === 1 ? "" : "s"}`} />
                    </button>
                    {canReorder && groups.length > 1 && (
                      <span className="session-group-move">
                        <Button appearance="ghost" size="small" icon={UpIcon} aria-label={`Move ${name} group up`} title="Move group up" disabled={gi === 0} onClick={() => applyGroups(moveGroupBy(groups, g.persona, -1))} />
                        <Button appearance="ghost" size="small" icon={DownIcon} aria-label={`Move ${name} group down`} title="Move group down" disabled={gi === groups.length - 1} onClick={() => applyGroups(moveGroupBy(groups, g.persona, 1))} />
                      </span>
                    )}
                  </div>
                  {open && (
                    <ul className="session-list">
                      {g.sessions.map((s, si) => row(s, (
                        <>
                          {canReorder && (
                            <>
                              <Button appearance="ghost" size="small" icon={UpIcon} aria-label={`Move ${s.title} up`} title="Move up" disabled={si === 0} onClick={() => applyGroups(moveSessionBy(groups, s.id, -1))} />
                              <Button appearance="ghost" size="small" icon={DownIcon} aria-label={`Move ${s.title} down`} title="Move down" disabled={si === g.sessions.length - 1} onClick={() => applyGroups(moveSessionBy(groups, s.id, 1))} />
                            </>
                          )}
                          {exportButton(s)}
                          <Button appearance="ghost" size="small" icon={EditIcon} aria-label={`Rename ${s.title}`} title="Rename session" onClick={() => setRenaming({ session: s, title: s.title })} />
                          <Button appearance="ghost" size="small" icon={TrashIcon} aria-label={`Archive ${s.title}`} title="Archive session" onClick={() => setConfirmArchive(s)} />
                        </>
                      ), canReorder ? {
                        onStart: () => setDragging({ kind: "session", id: s.id }),
                        onEnd: () => setDragging(null),
                        onDrop: () => { if (dragging?.kind === "session") applyGroups(dropSession(groups, dragging.id, s.id)); setDragging(null); },
                      } : undefined))}
                    </ul>
                  )}
                </section>
              );
            })}
          </div>
        </aside>
        <ColumnDivider label="Resize session list" width={listWidth} min={SIDE_MIN} max={SIDE_MAX} direction={1} onChange={resizeList} />
        <section className="pane pane-thread" aria-label="Conversation">
          {!selected ? (
            <Text subdued>Select a session to see its details.</Text>
          ) : (
            <>
              <div className="thread-head">
                <div>
                  <Text variant="headline" el="h3" size="medium">{selected.title}</Text>
                  <span className="thread-status"><StatusIcon status={selected.status} /><Text size="small" subdued>{selected.workersRunning && selected.status === "idle" ? "Working (background)" : selected.status}</Text></span>
                </div>
                {canResume && (
                  <Button
                    appearance="secondary"
                    disabled={!!blocked}
                    onClick={() => run(async () => {
                      await api.resume(selected.id);
                      setPausedIds((ids) => ids.filter((id) => id !== selected.id));
                      await onChanged();
                    })}
                  >
                    Resume session
                  </Button>
                )}
                {canPause && (
                  <Button
                    appearance="secondary"
                    onClick={() => run(async () => {
                      await api.pause(selected.id);
                      setPausedIds((ids) => [...ids, selected.id]);
                      await onChanged();
                    })}
                  >
                    Pause session
                  </Button>
                )}
              </div>
              {blocked && (
                <div id="budget-block">
                  <Alert status="warning" title={blocked === "session" ? "Add budget to continue" : "Sending is paused until midnight except for the Controller"} role="status" />
                </div>
              )}
              {transcript.fault && <Alert status="warning" title={transcript.fault} role="alert" />}
              <div className="thread-log" ref={logRef} onScroll={onLogScroll} role="log" aria-label="Messages">
                <div className="thread-items" ref={itemsRef}>
                {transcript.hiddenCount > 0 && (
                  <Button appearance="ghost" size="small" onClick={transcript.loadMore}>
                    Load earlier messages ({transcript.hiddenCount})
                  </Button>
                )}
                <Thread
                  items={transcript.items}
                  progress={transcript.progress}
                  connected={transcript.connected}
                  questions={pending}
                  outgoing={outgoing}
                  onChanged={onChanged}
                />
                </div>
              </div>
              <div
                className="composer-divider"
                role="separator"
                aria-orientation="horizontal"
                aria-label="Resize message box"
                aria-valuemin={COMPOSER_MIN}
                aria-valuemax={COMPOSER_MAX}
                aria-valuenow={composerHeight}
                tabIndex={0}
                onKeyDown={(e) => {
                  if (e.key === "ArrowUp") { e.preventDefault(); resizeComposer(composerHeight + 24); }
                  if (e.key === "ArrowDown") { e.preventDefault(); resizeComposer(composerHeight - 24); }
                }}
                onPointerDown={(e) => {
                  e.preventDefault();
                  const startY = e.clientY;
                  const startH = composerHeight;
                  const move = (ev: PointerEvent) => resizeComposer(startH + (startY - ev.clientY));
                  const up = () => {
                    window.removeEventListener("pointermove", move);
                    window.removeEventListener("pointerup", up);
                  };
                  window.addEventListener("pointermove", move);
                  window.addEventListener("pointerup", up);
                }}
              />
              <Composer
                key={selected.id}
                sessionId={selected.id}
                sending={sending}
                blocked={blocked}
                hint={blocked ? (blocked === "session" ? "Add budget to continue" : "Sending is paused until midnight except for the Controller") : undefined}
                height={composerHeight}
                onSend={send}
              />
            </>
          )}
        </section>
        <ColumnDivider className="column-divider-attrs" label="Resize session details" width={attrsWidth} min={SIDE_MIN} max={SIDE_MAX} direction={-1} onChange={resizeAttrs} />
        <aside className="pane pane-attrs" aria-label="Session details">
          {selected && (() => {
            const repos = selected.repos?.length ? selected.repos : selected.repo ? [selected.repo] : [];
            const branches = selected.branches?.length ? selected.branches : selected.branch ? [selected.branch] : [];
            return (
            <>
              <Text variant="headline" el="h4" size="small">Estimated cost</Text>
              <CostBlock
                cost={cost}
                raises={(
                  <div className="cost-raises" role="group" aria-label="Add budget">
                    {([25, 100] as const).map((n) => (
                      <Button
                        key={n}
                        appearance="secondary"
                        size="small"
                        aria-label={`+$${n} to today's limit`}
                        disabled={!cost?.canExtend || blocked === "fyrd" || raising}
                        onClick={() => addBudget(selected.id, cost!.limitUsd, n)}
                      >
                        +${n}
                      </Button>
                    ))}
                  </div>
                )}
              />
              <Text variant="headline" el="h4" size="small">Details</Text>
              <dl className="details">
                <dt>Role</dt><dd>{isController(selected) ? "Controller" : selected.role}</dd>
                <dt>Repository</dt><dd>{repos.length === 0 ? "None attached" : <ul className="detail-list">{repos.map((r) => <li key={r}>{r}</li>)}</ul>}</dd>
                <dt>Branch</dt><dd>{branches.length === 0 ? "None" : <ul className="detail-list">{branches.map((b) => <li key={b}>{b}</li>)}</ul>}</dd>
                <dt>Started</dt><dd title={fullTime(selected.createdAt)}>{relativeTime(selected.createdAt)}</dd>
                <dt>Last activity</dt><dd title={fullTime(selected.lastActivity)}>{relativeTime(selected.lastActivity)}</dd>
              </dl>
              <AddRepo key={selected.id} sessionId={selected.id} />
              <Text variant="headline" el="h4" size="small">Pull requests</Text>
              {gitHubNote && <Text size="small" subdued role="note">{gitHubNote}</Text>}
              {chains.map((c) => (
                <Text key={c.prs[0].url} size="small" subdued className="pr-chain" title={c.fork ? "This stack forks: it can be merged one PR at a time" : undefined}>
                  {chainLine(c)}{c.fork ? " · forked" : ""}
                </Text>
              ))}
              {selected.pullRequests.length === 0 ? (gitHubNote ? null : <Text subdued>—</Text>) : (
                <ul className="pr-list">
                  {selected.pullRequests.map((u) => (
                    <PullRequestItem
                      key={u}
                      url={u}
                      status={prStatuses.find((p) => p.url === u) ?? listed(u)}
                      alsoOn={all.filter((o) => o.id !== selected.id && o.pullRequests.includes(u))}
                      onOpenSession={(id) => navigate("sessions", id)}
                      actionsHidden={gitHubNote !== null}
                      error={prErrors[u]}
                      notice={prNotices[u]}
                      nudges={(() => {
                        const st = prStatuses.find((p) => p.url === u) ?? listed(u);
                        return st ? nudgesFor(st, all, controller) : [];
                      })()}
                      onNudge={(n) => pickNudge(u, n)}
                      {...stackProps(u)}
                      onReady={() => prAction(u, () => api.markPrReady(selected.id, u))}
                      onMerge={() => setConfirmMerge(prStatuses.find((p) => p.url === u) ?? listed(u) ?? null)}
                    />
                  ))}
                </ul>
              )}
              <Text variant="headline" el="h4" size="small">Tags</Text>
              <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
                {selected.tags.length === 0 ? <Text subdued size="small">None</Text> : selected.tags.map((t) => <Chip key={t} label={t} size="small" />)}
              </div>
              {confirmStack && (
                <ScrollIntoView><Alert status="warning" title="Merge the whole stack?">
                  <Text>{confirmLine(confirmStack.top, confirmStack.rootBase)}</Text>
                  <div style={{ display: "flex", gap: 8 }}>
                    <Button appearance="primary" size="small" onClick={() => void startStack(confirmStack.url)}>Merge stack</Button>
                    <Button appearance="ghost" size="small" onClick={() => setConfirmStack(null)}>Cancel</Button>
                  </div>
                </Alert></ScrollIntoView>
              )}
              {confirmNudge && (
                <ScrollIntoView><Alert status="warning" title={confirmNudge.nudge.confirm ?? "Send this request?"}>
                  <Text>This starts a paid session through the Controller.</Text>
                  <div style={{ display: "flex", gap: 8 }}>
                    <Button appearance="primary" size="small" onClick={() => void sendNudge(confirmNudge.url, confirmNudge.nudge)}>
                      Start PR Monitor
                    </Button>
                    <Button appearance="ghost" size="small" onClick={() => setConfirmNudge(null)}>Cancel</Button>
                  </div>
                </Alert></ScrollIntoView>
              )}
              {confirmMerge && (() => {
                const warning = mergeOrderWarning(confirmMerge, sessionPrs);
                const merge = (force: boolean) => {
                  const pr = confirmMerge;
                  setConfirmMerge(null);
                  void prAction(pr.url, () => api.mergePr(selected.id, pr.url, force));
                };
                return (
                  <ScrollIntoView><Alert status="warning" title={`Merge ${prLabel(confirmMerge.url)} ${confirmMerge.title}?`}>
                    {warning
                      ? <Text role="alert">{warning}</Text>
                      : <Text>This squash-merges {prLabel(confirmMerge.url)} into <code>{confirmMerge.base || "its base"}</code> on GitHub.</Text>}
                    <div style={{ display: "flex", gap: 8 }}>
                      <Button appearance="primary" size="small" onClick={() => merge(warning !== null)}>
                        {warning ? "Merge this only anyway" : "Merge pull request"}
                      </Button>
                      <Button appearance="ghost" size="small" onClick={() => setConfirmMerge(null)}>Cancel</Button>
                    </div>
                  </Alert></ScrollIntoView>
                );
              })()}
            </>
            );
          })()}
        </aside>
      </div>
    </div>
  );
}
