import { useState } from "react";
import { Alert, Button, Checkbox, Text, Textarea } from "@servicetitan/anvil2";
import * as api from "../api";
import type { HubMessage, HubQuestion, HubSessionView } from "../types";
import type { ScreenProps } from "./ScreenProps";

interface QuestionProps {
  q: HubQuestion;
  onAnswered: (waitingFor: number | null) => void;
  onChanged: ScreenProps["onChanged"];
}

function QuestionBlock({ q, onAnswered, onChanged }: QuestionProps) {
  const [text, setText] = useState("");
  const [picked, setPicked] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const send = async (body: { choice?: string; text?: string }) => {
    setBusy(true);
    setError(null);
    try {
      const res = await api.answerQuestion(q.id, body);
      onAnswered(res?.waitingFor ?? null);
      await onChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const stale = error !== null && /no longer waiting/i.test(error);
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      <Text variant="headline" el="h3" size="small">{q.question}</Text>
      {q.multiSelect ? (
        <fieldset style={{ border: 0, padding: 0, margin: 0 }}>
          <legend style={{ position: "absolute", left: -9999 }}>Options</legend>
          {q.options.map((o) => (
            <Checkbox
              key={o}
              label={o}
              checked={picked.includes(o)}
              onChange={(e) => setPicked(e.target.checked ? [...picked, o] : picked.filter((p) => p !== o))}
            />
          ))}
          <Button appearance="primary" disabled={busy || picked.length === 0} onClick={() => send({ text: picked.join(", ") })}>
            Send
          </Button>
        </fieldset>
      ) : (
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          {q.options.map((o) => (
            <Button key={o} appearance="secondary" disabled={busy} onClick={() => send({ choice: o })}>{o}</Button>
          ))}
        </div>
      )}
      <Textarea label="Your reply" rows={2} value={text} onChange={(e) => setText(e.target.value)} />
      <div>
        <Button appearance="primary" disabled={busy || text.trim() === ""} onClick={() => send({ text: text.trim() })}>
          Send reply
        </Button>
      </div>
      {error && (
        <Alert status="danger" title={error} role="alert">
          {stale && <a href={`https://claude.ai/code/${q.sessionId}`} target="_blank" rel="noreferrer">Open in claude.ai</a>}
        </Alert>
      )}
    </div>
  );
}

/** "just now", "5m ago", "3h ago", "2d ago". */
function age(iso: string, now = Date.now()): string {
  const ms = now - new Date(iso).getTime();
  if (!Number.isFinite(ms)) return "";
  const m = Math.floor(ms / 60_000);
  if (m < 1) return "just now";
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  return h < 24 ? `${h}h ago` : `${Math.floor(h / 24)}d ago`;
}

/** Collapsed by default; loads the session's recent messages each time it is expanded. */
function Conversation({ sessionId }: { sessionId: string }) {
  const [expanded, setExpanded] = useState(false);
  const [messages, setMessages] = useState<HubMessage[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const toggle = async () => {
    const next = !expanded;
    setExpanded(next);
    if (!next) return;
    setError(null);
    try {
      setMessages((await api.getMessages(sessionId)) ?? []);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  return (
    <div>
      <Button appearance="ghost" aria-expanded={expanded} onClick={toggle}>Conversation so far</Button>
      {expanded && (
        <div style={{ display: "flex", flexDirection: "column", gap: 6 }} role="log" aria-label="Conversation">
          {error && <Alert status="danger" title={error} role="alert" />}
          {messages === null && !error && <Text subdued>Loading…</Text>}
          {messages?.length === 0 && <Text subdued>No messages yet.</Text>}
          {messages?.map((m, i) => (
            <div key={i} className={m.role === "user" ? "msg-user" : "msg-agent"}>{m.text}</div>
          ))}
        </div>
      )}
    </div>
  );
}

function RequestCard({ group, session, onChanged }: {
  group: HubQuestion[]; session: HubSessionView | undefined; onChanged: ScreenProps["onChanged"];
}) {
  const [answered, setAnswered] = useState<string[]>([]);
  const [waitingFor, setWaitingFor] = useState<number | null>(null);
  const open = group.filter((q) => !answered.includes(q.id));
  const first = group[0];
  const meta = [first.sessionTitle, session?.repo, `asked ${age(first.askedAt)}`].filter(Boolean).join(" · ");
  return (
    <section className="card" aria-label={`Question from ${first.sessionTitle}`}>
      <Text variant="body" size="small" subdued>{meta}</Text>
      {open.map((q) => (
        <QuestionBlock
          key={q.id}
          q={q}
          onChanged={onChanged}
          onAnswered={(w) => {
            setAnswered((a) => [...a, q.id]);
            setWaitingFor(w);
          }}
        />
      ))}
      {waitingFor !== null && open.length > 0 && (
        <Text subdued>Answer the remaining {waitingFor} question(s) to send.</Text>
      )}
      <Conversation sessionId={first.sessionId} />
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
