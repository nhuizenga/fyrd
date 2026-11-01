import { Spinner, Text } from "@servicetitan/anvil2";
import type { HubQuestion, TranscriptItem } from "../types";
import CopyButton from "./CopyButton";
import Markdown from "./Markdown";
import QuestionGroup from "./QuestionGroup";

/** One transcript item as a chat bubble; a still-pending question gets its answer buttons. */
export default function ThreadItem({ item, pending, onChanged }: {
  item: TranscriptItem; pending: HubQuestion[]; onChanged: () => void | Promise<void>;
}) {
  if (item.kind === "progress") {
    return (
      <div className="msg msg-agent msg-progress">
        <Spinner size="small" role="status" aria-label="In progress" />
        <span>{item.text}</span>
      </div>
    );
  }
  if (item.kind === "question") {
    if (pending.length > 0) {
      return (
        <section className="msg msg-agent msg-question" aria-label="Question waiting for you">
          <QuestionGroup group={pending} onChanged={onChanged} />
        </section>
      );
    }
    return (
      <div className="msg msg-agent msg-question">
        {(item.questions ?? []).map((q) => (
          <div key={q.question}>
            <Text variant="headline" el="h4" size="small">{q.question}</Text>
            <ul className="msg-options">{q.options.map((o) => <li key={o.label}>{o.label}</li>)}</ul>
          </div>
        ))}
      </div>
    );
  }
  return (
    <div className={item.role === "user" ? "msg msg-user" : "msg msg-agent"}>
      <Markdown text={item.text} />
      <CopyButton text={item.text} />
    </div>
  );
}
