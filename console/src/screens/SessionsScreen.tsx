import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Alert, Button, Chip, Link, ProgressBar, SearchField, Text, TextField } from "@servicetitan/anvil2";
import ReadyIcon from "@servicetitan/anvil2/assets/icons/material/round/check_circle.svg?react";
import MergeIcon from "@servicetitan/anvil2/assets/icons/material/round/call_merge.svg?react";
import TrashIcon from "@servicetitan/anvil2/assets/icons/material/round/delete_outline.svg?react";
import OpenIcon from "@servicetitan/anvil2/assets/icons/material/round/open_in_new.svg?react";
import ReplaceIcon from "@servicetitan/anvil2/assets/icons/material/round/swap_horiz.svg?react";
import * as api from "../api";
import AddRepo from "../components/AddRepo";
import Composer, { DRAFT_KEY, readStored, writeStored } from "../components/Composer";
import Thread from "../components/Thread";
import StatusIcon from "../components/StatusIcon";
import type { HubCostView, HubSessionView, PullRequestStatus } from "../types";
import { lastSession, navigate, rememberSession, replaceRoute } from "../route";
import { fullTime, relativeTime } from "../time";
import { useTranscript } from "../useTranscript";
import type { ScreenProps } from "./ScreenProps";

const COMPOSER_KEY = "fyrd.composerHeight";
const COMPOSER_MIN = 72;
const COMPOSER_MAX = 480;
const COMPOSER_DEFAULT = 96;

const repoName = (repo: string) => repo.split("/").pop() || repo;
const prLabel = (url: string) => (/\/pull\/(\d+)/.exec(url)?.[1] ? `#${/\/pull\/(\d+)/.exec(url)![1]}` : url);

const usd = (n: number) => `$${n.toFixed(2)}`;
const pct = (ratio: number) => Math.round(ratio * 100);

/** The session's spend today, a bar against its daily limit, and the +$ raises. */
function CostBlock({ cost }: { cost: HubCostView | null | undefined }) {
  if (!cost) return <Text>—</Text>;
  return (
    <>
      <Text>{usd(cost.costToday)}{cost.estimated ? " (estimated)" : ""}</Text>
      <ProgressBar
        aria-label={`${pct(cost.ratio)} percent of today's ${usd(cost.limitUsd)} limit used`}
        value={Math.min(100, cost.ratio * 100)}
        max={100}
        error={cost.blocked !== null}
      />
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
  const [outbox, setOutbox] = useState<{ id: string; sessionId: string; text: string; itemsAtSend: number }[]>([]);
  const outboxSeq = useRef(0);
  const [error, setError] = useState<string | null>(null);
  const [pausedIds, setPausedIds] = useState<string[]>([]);
  const [confirmReplace, setConfirmReplace] = useState(false);
  const [prStatuses, setPrStatuses] = useState<PullRequestStatus[]>([]);
  const [confirmMerge, setConfirmMerge] = useState<PullRequestStatus | null>(null);
  const [confirmArchive, setConfirmArchive] = useState<HubSessionView | null>(null);
  const [raising, setRaising] = useState(false);
  // A raise's response, shown until the poll catches up (the server only requests a poll, so the next state can be stale).
  const [raised, setRaised] = useState<{ id: string; from: number; view: HubCostView } | null>(null);
  const [adoptId, setAdoptId] = useState("");
  const transcript = useTranscript(selectedId);
  // Optimistic messages show until the transcript has a newer user item with the same text.
  const outgoing = useMemo(
    () => outbox.filter((m) => m.sessionId === selectedId
      && !transcript.items.slice(m.itemsAtSend).some((i) => i.role === "user" && i.kind !== "question" && i.text.trim() === m.text)),
    [outbox, selectedId, transcript.items],
  );

  const logRef = useRef<HTMLDivElement>(null);
  const itemsRef = useRef<HTMLDivElement>(null);
  const following = useRef(true);
  const autoTop = useRef(0);

  const controllerId = state?.controller?.sessionId ?? null;
  // The Controller is adopted from claude.ai, so it carries no Fyrd tags: identify it by id.
  const isController = (s: HubSessionView) => s.id === controllerId;
  const all = state?.sessions ?? [];
  const controller = all.find(isController) ?? null;
  const others = all.filter((s) => !isController(s));
  const q = search.trim().toLowerCase();
  const sessions = q ? others.filter((s) => s.title.toLowerCase().includes(q)) : others;
  const selected = all.find((s) => s.id === selectedId) ?? null;
  const hasThread = selected !== null;
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
    void loadPrs();
  }, [loadPrs]);

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

  useLayoutEffect(stickToBottom, [stickToBottom, transcript.items.length, outgoing.length, selectedId, hasThread]);

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
        // Gone since the last poll: refreshing brings back the Adopt Controller panel.
        if ((e as { status?: number }).status !== 404) throw e;
      }
      await onChanged();
    });

  const adoptController = () =>
    run(async () => {
      await api.adoptController(adoptId.trim());
      setAdoptId("");
      await onChanged();
    });

  const archiveSession = (s: HubSessionView) =>
    run(async () => {
      setConfirmArchive(null);
      await api.archiveSession(s.id);
      if (s.id === selectedId) navigate("sessions");
      await onChanged();
    });

  const prAction = (fn: () => Promise<unknown>) =>
    run(async () => {
      await fn();
      await loadPrs();
      await onChanged();
    });

  const replaceController = () =>
    run(async () => {
      setConfirmReplace(false);
      await api.retireController();
      await onChanged();
    });

  const cost = selected && raised?.id === selected.id && selected.cost?.limitUsd === raised.from ? raised.view : selected?.cost;
  const blocked = cost?.blocked ?? null;
  const sending = selected ? sendingIds.includes(selected.id) : false;
  const itemsCount = useRef(0);
  itemsCount.current = transcript.items.length;
  const send = async (text: string): Promise<boolean> => {
    if (!selected) return false;
    const sentTo = selected.id;
    const entry = { id: `out-${(outboxSeq.current += 1)}`, sessionId: sentTo, text, itemsAtSend: itemsCount.current };
    following.current = true;
    setSendingIds((ids) => [...ids, sentTo]);
    setOutbox((o) => [...o, entry]);
    setError(null);
    const drop = () => setOutbox((o) => o.filter((x) => x.id !== entry.id));
    try {
      await api.sendMessage(sentTo, text);
      // The echoed message normally replaces the optimistic one; if it never matches, don't leave a duplicate behind.
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

  const row = (s: HubSessionView, actions?: ReactNode) => (
    <li key={s.id} className="session-item">
      <button type="button" className="row-btn session-row" aria-current={s.id === selectedId} onClick={() => navigate("sessions", s.id)}>
        <StatusIcon status={s.status} />
        <span className="session-row-text">
          <span className="session-row-title">{s.title}</span>
          {(s.repo || s.pullRequests.length > 0) && (
            <span className="session-row-chips">
              {s.repo && <Chip label={repoName(s.repo)} size="small" title={s.repo} />}
              {s.pullRequests.map((u) => <Chip key={u} label={prLabel(u)} size="small" title={u} />)}
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
      </div>
      <div className="sessions-grid">
        <aside className="pane pane-list" aria-label="Session list">
          {controller && (
            <ul className="session-list">
              {row(controller, (
                <>
                  <Button appearance="ghost" size="small" icon={OpenIcon} aria-label="Open Controller" title="Open Controller in claude.ai" onClick={openController} />
                  <Button appearance="ghost" size="small" icon={ReplaceIcon} aria-label="Replace Controller" title="Replace Controller" onClick={() => setConfirmReplace(true)} />
                </>
              ))}
            </ul>
          )}
          {confirmReplace && (
            <Alert status="warning" title="Replace the Controller?">
              <Text>This archives the current Controller. You'll then adopt a new one.</Text>
              <div style={{ display: "flex", gap: 8 }}>
                <Button appearance="primary" size="small" onClick={replaceController}>Archive and replace</Button>
                <Button appearance="ghost" size="small" onClick={() => setConfirmReplace(false)}>Cancel</Button>
              </div>
            </Alert>
          )}
          {state && !state.controller && (
            <section className="adopt" aria-labelledby="adopt-h">
              <Text variant="headline" el="h3" size="small" id="adopt-h">Adopt Controller</Text>
              <Text size="small">
                Create a new session on claude.ai (no repo needed), then paste its session id here.{" "}
                <Link href="https://claude.ai/code" target="_blank" rel="noopener noreferrer">Open claude.ai/code</Link>
              </Text>
              <TextField label="Session id" placeholder="session_..." value={adoptId} onChange={(e) => setAdoptId(e.target.value)} />
              <div>
                <Button appearance="primary" size="small" disabled={adoptId.trim() === ""} onClick={adoptController}>Adopt</Button>
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
          <ul className="session-list pane-scroll">
            {sessions.map((s) => row(s, (
              <Button appearance="ghost" size="small" icon={TrashIcon} aria-label={`Archive ${s.title}`} title="Archive session" onClick={() => setConfirmArchive(s)} />
            )))}
          </ul>
        </aside>
        <section className="pane pane-thread" aria-label="Conversation">
          {!selected ? (
            <Text subdued>Select a session to see its details.</Text>
          ) : (
            <>
              <div className="thread-head">
                <div>
                  <Text variant="headline" el="h3" size="medium">{selected.title}</Text>
                  <span className="thread-status"><StatusIcon status={selected.status} /><Text size="small" subdued>{selected.status}</Text></span>
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
        <aside className="pane pane-attrs" aria-label="Session details">
          {selected && (
            <>
              <Text variant="headline" el="h4" size="small">Details</Text>
              <dl className="details">
                <dt>Role</dt><dd>{isController(selected) ? "Controller" : selected.role}</dd>
                <dt>Repository</dt><dd>{selected.repo ?? "None attached"}</dd>
                <dt>Started</dt><dd title={fullTime(selected.createdAt)}>{relativeTime(selected.createdAt)}</dd>
                <dt>Last activity</dt><dd title={fullTime(selected.lastActivity)}>{relativeTime(selected.lastActivity)}</dd>
              </dl>
              <AddRepo key={selected.id} sessionId={selected.id} />
              <Text variant="headline" el="h4" size="small">Estimated cost</Text>
              <CostBlock cost={cost} />
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }} role="group" aria-label="Add budget">
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
              {cost && !cost.exempt && !cost.canExtend && <Text size="small" subdued>Raises unlock at 90% of today's limit.</Text>}
              <Text variant="headline" el="h4" size="small">Tags</Text>
              <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
                {selected.tags.length === 0 ? <Text subdued size="small">None</Text> : selected.tags.map((t) => <Chip key={t} label={t} size="small" />)}
              </div>
              <Text variant="headline" el="h4" size="small">Pull requests</Text>
              {selected.pullRequests.length === 0 ? <Text subdued>—</Text> : (
                <ul className="pr-list">
                  {selected.pullRequests.map((u) => {
                    const st = prStatuses.find((p) => p.url === u);
                    const label = prLabel(u);
                    return (
                      <li key={u} className="pr-item">
                        <Link href={u} target="_blank" rel="noopener noreferrer">{label}</Link>
                        {st && st.state !== "unknown" && (
                          <span className="pr-badges">
                            {st.draft && <Chip label="Draft" size="small" />}
                            {st.state !== "open" && <Chip label={st.state} size="small" />}
                            {st.state === "open" && !st.draft && st.checks !== "none" && <Chip label={`Checks ${st.checks}`} size="small" />}
                          </span>
                        )}
                        {st?.state === "open" && st.draft && (
                          <Button
                            appearance="ghost" size="small" icon={ReadyIcon}
                            aria-label={`Mark ${label} ready for review`} title="Mark ready for review"
                            className="pr-ready"
                            onClick={() => prAction(() => api.markPrReady(selected.id, u))}
                          />
                        )}
                        {st?.canMerge && (
                          <Button
                            appearance="ghost" size="small" icon={MergeIcon}
                            aria-label={`Merge ${label}`} title="Merge pull request"
                            onClick={() => setConfirmMerge(st)}
                          />
                        )}
                      </li>
                    );
                  })}
                </ul>
              )}
              {confirmMerge && (
                <Alert status="warning" title={`Merge ${prLabel(confirmMerge.url)} ${confirmMerge.title}?`}>
                  <Text>This merges the pull request on GitHub.</Text>
                  <div style={{ display: "flex", gap: 8 }}>
                    <Button
                      appearance="primary" size="small"
                      onClick={() => {
                        const pr = confirmMerge;
                        setConfirmMerge(null);
                        void prAction(() => api.mergePr(selected.id, pr.url));
                      }}
                    >
                      Merge pull request
                    </Button>
                    <Button appearance="ghost" size="small" onClick={() => setConfirmMerge(null)}>Cancel</Button>
                  </div>
                </Alert>
              )}
            </>
          )}
        </aside>
      </div>
    </div>
  );
}
