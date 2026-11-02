import { memo, useMemo } from "react";
import { Text } from "@servicetitan/anvil2";
import type { HubQuestion, TranscriptItem } from "../types";
import QuestionGroup from "./QuestionGroup";
import ThreadItem from "./ThreadItem";

export interface OutgoingMessage { id: string; text: string }

const Item = memo(ThreadItem);

/**
 * The transcript bubbles. Memoized, so it re-renders only when the transcript, the pending questions or the outbox
 * change, never when the screen re-renders for something unrelated such as typing.
 */
function Thread({ items, progress, connected, questions, outgoing, onChanged }: {
  items: TranscriptItem[];
  progress: TranscriptItem | null;
  connected: boolean;
  questions: HubQuestion[];
  outgoing: OutgoingMessage[];
  onChanged: () => void | Promise<void>;
}) {
  const none: HubQuestion[] = useMemo(() => [], []);
  const byTool = useMemo(() => {
    const m = new Map<string, HubQuestion[]>();
    for (const q of questions) m.set(q.toolUseId, [...(m.get(q.toolUseId) ?? []), q]);
    return m;
  }, [questions]);
  const shown = new Set(items.filter((i) => i.kind === "question").map((i) => i.toolUseId));
  // Questions the stream hasn't delivered yet (or never will, if it faults) still get answer buttons.
  const unshown = questions.filter((x) => !shown.has(x.toolUseId));

  return (
    <>
      {items.length === 0 && !progress && unshown.length === 0 && outgoing.length === 0 && (
        <Text subdued>{connected ? "No messages yet." : "Loading…"}</Text>
      )}
      {items.map((item) => (
        <Item key={item.key} item={item} pending={item.kind === "question" ? byTool.get(item.toolUseId ?? "") ?? none : none} onChanged={onChanged} />
      ))}
      {outgoing.map((m) => (
        <div key={m.id} className="msg msg-user msg-sending" aria-label="Sending" aria-busy="true">
          <div className="md"><p>{m.text}</p></div>
        </div>
      ))}
      {progress && <Item item={progress} pending={none} onChanged={onChanged} />}
      {unshown.length > 0 && (
        <section className="msg msg-agent msg-question" aria-label="Question waiting for you">
          <QuestionGroup group={unshown} onChanged={onChanged} />
        </section>
      )}
    </>
  );
}

export default memo(Thread);
