import { useState } from "react";
import { Alert, Button, Checkbox, Text, Textarea } from "@servicetitan/anvil2";
import * as api from "../api";
import type { HubQuestion } from "../types";
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

function RequestCard({ group, onChanged }: { group: HubQuestion[]; onChanged: ScreenProps["onChanged"] }) {
  const [answered, setAnswered] = useState<string[]>([]);
  const [waitingFor, setWaitingFor] = useState<number | null>(null);
  const open = group.filter((q) => !answered.includes(q.id));
  return (
    <section className="card" aria-label={`Question from ${group[0].sessionTitle}`}>
      <Text variant="body" size="small" subdued>{group[0].sessionTitle}</Text>
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
        [...groups.entries()].map(([key, g]) => <RequestCard key={key} group={g} onChanged={onChanged} />)
      )}
    </>
  );
}
