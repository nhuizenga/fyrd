import { useState } from "react";
import { Alert, Button, Checkbox, Text, Textarea } from "@servicetitan/anvil2";
import * as api from "../api";
import type { HubQuestion } from "../types";

type OnChanged = () => void | Promise<void>;

interface QuestionProps {
  q: HubQuestion;
  onAnswered: (waitingFor: number | null) => void;
  onChanged: OnChanged;
}

function QuestionBlock({ q, onAnswered, onChanged }: QuestionProps) {
  const [text, setText] = useState("");
  const [picked, setPicked] = useState<string[]>([]);
  const [other, setOther] = useState(false);
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
  const otherText = text.trim();
  const multiAnswer = [...picked, ...(other && otherText ? [otherText] : [])];
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      <Text variant="headline" el="h3" size="small">{q.question}</Text>
      {q.multiSelect ? (
        <fieldset style={{ border: 0, padding: 0, margin: 0, display: "flex", flexDirection: "column", gap: 4 }}>
          <legend style={{ position: "absolute", left: -9999 }}>Options</legend>
          {q.options.map((o) => (
            <Checkbox
              key={o}
              label={o}
              checked={picked.includes(o)}
              onChange={(e) => setPicked(e.target.checked ? [...picked, o] : picked.filter((p) => p !== o))}
            />
          ))}
          <Checkbox label="Other" checked={other} onChange={(e) => setOther(e.target.checked)} />
        </fieldset>
      ) : (
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          {q.options.map((o) => (
            <Button key={o} appearance="secondary" disabled={busy} onClick={() => send({ choice: o })}>{o}</Button>
          ))}
          <Button appearance={other ? "primary" : "ghost"} aria-pressed={other} disabled={busy} onClick={() => setOther(!other)}>
            Other
          </Button>
        </div>
      )}
      {other && (
        <Textarea label="Your answer" rows={2} value={text} autoFocus onChange={(e) => setText(e.target.value)} />
      )}
      {q.multiSelect ? (
        <div>
          <Button appearance="primary" disabled={busy || multiAnswer.length === 0} onClick={() => send({ text: multiAnswer.join(", ") })}>
            Send
          </Button>
        </div>
      ) : other && (
        <div>
          <Button appearance="primary" disabled={busy || otherText === ""} onClick={() => send({ text: otherText })}>Send answer</Button>
        </div>
      )}
      {error && (
        <Alert status="danger" title={error} role="alert">
          {stale && <a href={`https://claude.ai/code/${q.sessionId}`} target="_blank" rel="noreferrer">Open in claude.ai</a>}
        </Alert>
      )}
    </div>
  );
}

/** The pending questions of one AskUserQuestion request, answerable with buttons. Used by the Inbox and the session thread. */
export default function QuestionGroup({ group, onChanged }: { group: HubQuestion[]; onChanged: OnChanged }) {
  const [answered, setAnswered] = useState<string[]>([]);
  const [waitingFor, setWaitingFor] = useState<number | null>(null);
  const open = group.filter((q) => !answered.includes(q.id));
  return (
    <>
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
    </>
  );
}
