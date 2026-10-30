import { useRef, useState, type ReactNode } from "react";
import * as api from "../api";
import type { InboxCard as Card, MemoryScope } from "../types";

interface Props {
  card: Card;
  specLabel: string;
  onAnswered: (relayError?: string) => void;
}

const URL_RE = /(https?:\/\/[^\s<>"']+)/g;

/** Splits text into plain strings and http(s) links; never builds HTML. */
function linkify(text: string): ReactNode[] {
  return text.split(URL_RE).map((part, i) =>
    i % 2 === 1 ? (
      <a key={i} href={part} target="_blank" rel="noreferrer">{part}</a>
    ) : (
      part
    ),
  );
}

export default function InboxCard({ card, specLabel, onAnswered }: Props) {
  const [busy, setBusy] = useState(false);
  const inFlight = useRef(false); // blocks a second click in the same tick, before re-render
  const [error, setError] = useState<string | null>(null);
  const [remember, setRemember] = useState(true);
  const [scope, setScope] = useState<MemoryScope>("Repo");
  const [text, setText] = useState("");
  const [showDetails, setShowDetails] = useState(false);

  async function send(answerText: string) {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setError(null);
    try {
      const res = await api.answer(card.id, { text: answerText, remember, scope });
      onAnswered(res?.relayError);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  }

  const label = card.unitId ? `${specLabel} · ${card.unitId}` : specLabel;

  return (
    <article className="card">
      <div className="card-label">{label}</div>
      <h3 className="card-question">{card.question}</h3>
      {card.recommendation && (
        <p className="card-pick">
          {`Lead's pick: ${card.recommendation}${card.reason ? ` — ${card.reason}` : ""}`}
        </p>
      )}
      {card.details && (
        <>
          <button type="button" className="link" onClick={() => setShowDetails((v) => !v)}>
            Details
          </button>
          {showDetails && <p className="card-details">{linkify(card.details)}</p>}
        </>
      )}
      <div className="card-options">
        {card.options.map((o) => (
          <button key={o} type="button" disabled={busy} onClick={() => void send(o)}>
            {o}
          </button>
        ))}
      </div>
      {card.allowFreeText && (
        <form
          className="card-free"
          onSubmit={(e) => {
            e.preventDefault();
            if (text.trim()) void send(text.trim());
          }}
        >
          <input
            type="text"
            aria-label="Your answer"
            value={text}
            disabled={busy}
            onChange={(e) => setText(e.target.value)}
          />
          <button type="submit" disabled={busy || !text.trim()}>
            Send
          </button>
        </form>
      )}
      <div className="card-remember">
        <label>
          <input
            type="checkbox"
            checked={remember}
            disabled={busy}
            onChange={(e) => setRemember(e.target.checked)}
          />{" "}
          Remember this
        </label>
        <select
          aria-label="Memory scope"
          value={scope}
          disabled={busy || !remember}
          onChange={(e) => setScope(e.target.value as MemoryScope)}
        >
          <option value="Repo">Repo</option>
          <option value="Global">Everywhere</option>
        </select>
      </div>
      {error && <p className="error" role="alert">{error}</p>}
    </article>
  );
}
