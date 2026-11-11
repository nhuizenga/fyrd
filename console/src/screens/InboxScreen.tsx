import { useLayoutEffect, useRef, useState } from "react";
import { Alert, Button, Flex, Text } from "@servicetitan/anvil2";
import * as api from "../api";
import Composer from "../components/Composer";
import QuestionGroup from "../components/QuestionGroup";
import ThreadItem from "../components/ThreadItem";
import { relativeTime } from "../time";
import { routeHref } from "../route";
import type { HubNotification, HubQuestion, HubSessionView } from "../types";
import { useTranscript } from "../useTranscript";
import type { ScreenProps } from "./ScreenProps";

/**
 * The session's live conversation. Collapsed it shows only the last few lines; expanded it scrolls on its own,
 * so the response below stays in view. The pending question itself is left out: the answer buttons show it.
 */
function Conversation({ sessionId, pendingToolUseIds }: { sessionId: string; pendingToolUseIds: Set<string> }) {
  const [expanded, setExpanded] = useState(false);
  const { items, connected, fault } = useTranscript(sessionId);
  const logRef = useRef<HTMLDivElement>(null);
  const shown = items.filter((i) => !(i.kind === "question" && pendingToolUseIds.has(i.toolUseId ?? "")));

  // Always show the newest lines, including after late layout (fonts, markdown) changes the height.
  useLayoutEffect(() => {
    const el = logRef.current;
    if (!el) return;
    const toEnd = () => { el.scrollTop = el.scrollHeight; };
    toEnd();
    const observer = new ResizeObserver(toEnd);
    for (const child of el.children) observer.observe(child);
    return () => observer.disconnect();
  }, [shown.length, expanded]);

  return (
    <div className="inbox-convo">
      {fault && <Alert status="warning" title={fault} role="alert" />}
      <div ref={logRef} className={expanded ? "inbox-log inbox-log-expanded" : "inbox-log"} role="log" aria-label="Conversation">
        {shown.length === 0 && <Text subdued size="small">{connected ? "No messages yet." : "Loading…"}</Text>}
        {shown.map((item) => <ThreadItem key={item.key} item={item} pending={[]} onChanged={() => {}} />)}
      </div>
      {shown.length > 0 && (
        <div>
          <Button appearance="ghost" size="small" aria-expanded={expanded} onClick={() => setExpanded(!expanded)}>
            {expanded ? "Show less" : "Show whole conversation"}
          </Button>
        </div>
      )}
    </div>
  );
}

function RequestCard({ group, session, onChanged }: {
  group: HubQuestion[]; session: HubSessionView | undefined; onChanged: ScreenProps["onChanged"];
}) {
  const first = group[0];
  const meta = [first.sessionTitle, session?.repo, `asked ${relativeTime(first.askedAt)}`].filter(Boolean).join(" · ");
  return (
    <section className="card" aria-label={`Question from ${first.sessionTitle}`}>
      <Text variant="body" size="small" subdued>{meta}</Text>
      <Conversation sessionId={first.sessionId} pendingToolUseIds={new Set(group.map((q) => q.toolUseId))} />
      <QuestionGroup group={group} onChanged={onChanged} />
    </section>
  );
}

/**
 * A session waiting on a plain-text question. Replying sends an ordinary message (it is not an AskUserQuestion answer) and
 * clears the card; Dismiss clears it without replying.
 */
function NotificationCard({ n, session, onChanged }: {
  n: HubNotification; session: HubSessionView | undefined; onChanged: ScreenProps["onChanged"];
}) {
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const meta = [n.sessionTitle, session?.repo, `waiting since ${relativeTime(n.at)}`].filter(Boolean).join(" · ");
  const blocked = session?.cost?.blocked ?? null;

  const act = async (run: () => Promise<unknown>) => {
    setSending(true);
    setError(null);
    try {
      await run();
      await onChanged();
      return true;
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      return false;
    } finally {
      setSending(false);
    }
  };

  return (
    <section className="card" aria-label={`Notification from ${n.sessionTitle}`}>
      <Text variant="body" size="small" subdued>{meta}</Text>
      <Text variant="headline" el="h3" size="small">{n.headline}</Text>
      <Conversation sessionId={n.sessionId} pendingToolUseIds={new Set()} />
      {error && <Alert status="danger" title={error} role="alert" />}
      <Composer
        key={n.sessionId}
        sessionId={n.sessionId}
        sending={sending}
        blocked={blocked}
        hint={blocked ? "Add budget to continue" : undefined}
        height={72}
        onSend={(text, fileIds) => act(() => (fileIds.length ? api.sendMessage(n.sessionId, text, fileIds) : api.sendMessage(n.sessionId, text)))}
      />
      <Flex gap="2">
        <Button appearance="ghost" size="small" disabled={sending} onClick={() => void act(() => api.dismissNotification(n.id))}>
          Dismiss
        </Button>
        <a href={routeHref("sessions", n.sessionId)}>Open session</a>
      </Flex>
    </section>
  );
}

export default function InboxScreen({ state, onChanged }: ScreenProps) {
  const questions = state?.questions ?? [];
  const notifications = state?.notifications ?? [];
  const groups = new Map<string, HubQuestion[]>();
  for (const q of questions) {
    const key = `${q.sessionId}:${q.requestId}`;
    groups.set(key, [...(groups.get(key) ?? []), q]);
  }
  return (
    <>
      <Text variant="headline" el="h2" size="large">Inbox</Text>
      {groups.size === 0 && notifications.length === 0 ? (
        <div className="card">
          <Text variant="headline" el="h3" size="small">You're all caught up</Text>
          <Text>No sessions are waiting on you. New requests show up here.</Text>
        </div>
      ) : (
        <>
          {[...groups.entries()].map(([key, g]) => (
            <RequestCard key={key} group={g} session={state?.sessions.find((s) => s.id === g[0].sessionId)} onChanged={onChanged} />
          ))}
          {notifications.map((n) => (
            <NotificationCard key={n.id} n={n} session={state?.sessions.find((s) => s.id === n.sessionId)} onChanged={onChanged} />
          ))}
        </>
      )}
    </>
  );
}
