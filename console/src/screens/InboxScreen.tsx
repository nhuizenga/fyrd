import { useLayoutEffect, useRef, useState } from "react";
import { Alert, Button, Text } from "@servicetitan/anvil2";
import QuestionGroup from "../components/QuestionGroup";
import ThreadItem from "../components/ThreadItem";
import { relativeTime } from "../time";
import type { HubQuestion, HubSessionView } from "../types";
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

export default function InboxScreen({ state, onChanged }: ScreenProps) {
  const questions = state?.questions ?? [];
  const groups = new Map<string, HubQuestion[]>();
  for (const q of questions) {
    const key = `${q.sessionId}:${q.requestId}`;
    groups.set(key, [...(groups.get(key) ?? []), q]);
  }
  return (
    <>
      <Text variant="headline" el="h2" size="large">Inbox</Text>
      {groups.size === 0 ? (
        <div className="card">
          <Text variant="headline" el="h3" size="small">You're all caught up</Text>
          <Text>No sessions are waiting on you. New requests show up here.</Text>
        </div>
      ) : (
        [...groups.entries()].map(([key, g]) => (
          <RequestCard key={key} group={g} session={state?.sessions.find((s) => s.id === g[0].sessionId)} onChanged={onChanged} />
        ))
      )}
    </>
  );
}
