import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Alert, Button, Chip, Link, SearchField, Text, TextField, Textarea } from "@servicetitan/anvil2";
import OpenIcon from "@servicetitan/anvil2/assets/icons/material/round/open_in_new.svg?react";
import ReplaceIcon from "@servicetitan/anvil2/assets/icons/material/round/swap_horiz.svg?react";
import * as api from "../api";
import AddRepo from "../components/AddRepo";
import QuestionGroup from "../components/QuestionGroup";
import ThreadItem from "../components/ThreadItem";
import StatusIcon from "../components/StatusIcon";
import type { HubSessionView } from "../types";
import { lastSession, navigate, rememberSession, replaceRoute } from "../route";
import { fullTime, relativeTime } from "../time";
import { useTranscript } from "../useTranscript";
import type { ScreenProps } from "./ScreenProps";

/** How close to the bottom (px) still counts as "following" the thread. */
const FOLLOW_SLACK = 48;

export default function SessionsScreen({ state, onChanged, routeId }: ScreenProps) {
  const [search, setSearch] = useState("");
  const selectedId = routeId ?? null;
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [pausedIds, setPausedIds] = useState<string[]>([]);
  const [confirmReplace, setConfirmReplace] = useState(false);
  const [adoptId, setAdoptId] = useState("");
  const transcript = useTranscript(selectedId);
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

  useLayoutEffect(stickToBottom, [stickToBottom, transcript.items.length, selectedId, hasThread]);

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

  const replaceController = () =>
    run(async () => {
      setConfirmReplace(false);
      await api.retireController();
      await onChanged();
    });

  const send = () => {
    if (!selected || sending || draft.trim() === "") return;
    const text = draft.trim();
    following.current = true;
    setSending(true);
    void run(async () => {
      try {
        await api.sendMessage(selected.id, text);
        setDraft("");
      } finally {
        setSending(false);
      }
    });
  };

  const pending = selected ? (state?.questions ?? []).filter((x) => x.sessionId === selected.id) : [];
  const shownToolUses = new Set(transcript.items.filter((i) => i.kind === "question").map((i) => i.toolUseId));
  // Questions the stream hasn't delivered yet (or never will, if it faults) still get answer buttons.
  const unshown = pending.filter((x) => !shownToolUses.has(x.toolUseId));
  const canPause = !!selected && !pausedIds.includes(selected.id) && (selected.status === "running" || selected.status === "requires_action");
  const canResume = !!selected && (pausedIds.includes(selected.id) || selected.status === "idle");

  const row = (s: HubSessionView, actions?: ReactNode) => (
    <li key={s.id} className="session-item">
      <button type="button" className="row-btn session-row" aria-current={s.id === selectedId} onClick={() => navigate("sessions", s.id)}>
        <StatusIcon status={s.status} />
        <span className="session-row-text">
          <span className="session-row-title">{s.title}</span>
          <Text size="small" subdued>{isController(s) ? "Controller" : s.role}</Text>
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
          <ul className="session-list pane-scroll">{sessions.map((s) => row(s))}</ul>
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
              {transcript.fault && <Alert status="warning" title={transcript.fault} role="alert" />}
              <div className="thread-log" ref={logRef} onScroll={onLogScroll} role="log" aria-label="Messages">
                <div className="thread-items" ref={itemsRef}>
                {transcript.items.length === 0 && !transcript.progress && unshown.length === 0 && (
                  <Text subdued>{transcript.connected ? "No messages yet." : "Loading…"}</Text>
                )}
                {transcript.items.map((item) => (
                  <ThreadItem
                    key={item.key}
                    item={item}
                    pending={item.kind === "question" ? pending.filter((x) => x.toolUseId === item.toolUseId) : []}
                    onChanged={onChanged}
                  />
                ))}
                {transcript.progress && (
                  <ThreadItem item={transcript.progress} pending={[]} onChanged={onChanged} />
                )}
                {unshown.length > 0 && (
                  <section className="msg msg-agent msg-question" aria-label="Question waiting for you">
                    <QuestionGroup group={unshown} onChanged={onChanged} />
                  </section>
                )}
                </div>
              </div>
              <div className="composer">
                <Textarea
                  label="Message this session"
                  hint="Enter to send, Shift+Enter for a new line"
                  rows={2}
                  value={draft}
                  onChange={(e) => setDraft(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
                      e.preventDefault();
                      send();
                    }
                  }}
                />
                <Button appearance="primary" disabled={sending || draft.trim() === ""} onClick={send}>Send</Button>
              </div>
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
              <Text>—</Text>
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }} role="group" aria-label="Add budget">
                <Button appearance="secondary" size="small" disabled>+$5</Button>
                <Button appearance="secondary" size="small" disabled>+$25</Button>
                <Button appearance="secondary" size="small" disabled>+$100</Button>
              </div>
              <Text variant="headline" el="h4" size="small">Tags</Text>
              <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
                {selected.tags.length === 0 ? <Text subdued size="small">None</Text> : selected.tags.map((t) => <Chip key={t} label={t} size="small" />)}
              </div>
              <Text variant="headline" el="h4" size="small">Pull requests</Text>
              <Text subdued>—</Text>
            </>
          )}
        </aside>
      </div>
    </div>
  );
}
