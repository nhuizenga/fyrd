import type { InboxCard as Card, SpecListEntry } from "../types";
import InboxCard from "./InboxCard";

interface Props {
  cards: Card[];
  specs: SpecListEntry[];
  onAnswered: () => void;
}

export default function InboxTab({ cards, specs, onAnswered }: Props) {
  if (cards.length === 0) {
    return <p className="empty">Nothing needs you right now.</p>;
  }
  const titleOf = (id: string) => specs.find((s) => s.id === id)?.run?.title ?? id;
  return (
    <div className="inbox">
      {cards.map((c) => (
        <InboxCard key={c.id} card={c} specLabel={titleOf(c.specId)} onAnswered={onAnswered} />
      ))}
    </div>
  );
}
