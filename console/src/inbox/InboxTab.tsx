import { useState } from "react";
import * as api from "../api";
import type { InboxCard as Card, SpecListEntry } from "../types";
import InboxCard from "./InboxCard";

interface Props {
  cards: Card[];
  undelivered?: Card[];
  specs: SpecListEntry[];
  onAnswered: (relayError?: string) => void;
}

function UndeliveredRow({ card, label, onRetried }: { card: Card; label: string; onRetried: (e?: string) => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function retry() {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const res = await api.relay(card.id);
      onRetried(res?.relayError);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <li>
      <div>
        <strong>{label}</strong>: {card.question} → {card.answer?.text}
        {card.answer?.relayError && <span className="relay-error">{` (${card.answer.relayError})`}</span>}
      </div>
      <button type="button" disabled={busy} onClick={() => void retry()}>Retry</button>
      {error && <p className="error" role="alert">{error}</p>}
    </li>
  );
}

export default function InboxTab({ cards, undelivered = [], specs, onAnswered }: Props) {
  if (cards.length === 0 && undelivered.length === 0) {
    return <p className="empty">Nothing needs you right now.</p>;
  }
  const titleOf = (id: string) => specs.find((s) => s.id === id)?.run?.title ?? id;
  return (
    <div className="inbox">
      {undelivered.length > 0 && (
        <section className="undelivered">
          <h3>Answers not delivered</h3>
          <ul>
            {undelivered.map((c) => (
              <UndeliveredRow key={c.id} card={c} label={titleOf(c.specId)} onRetried={onAnswered} />
            ))}
          </ul>
        </section>
      )}
      {cards.map((c) => (
        <InboxCard key={c.id} card={c} specLabel={titleOf(c.specId)} onAnswered={onAnswered} />
      ))}
    </div>
  );
}
