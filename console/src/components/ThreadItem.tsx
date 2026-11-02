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
        <div className="msg-progress-head">
          <Spinner size="small" role="status" aria-label="In progress" />
          <span>{item.status || "Working…"}</span>
        </div>
        {item.detail && <pre className="msg-progress-detail">{item.detail}</pre>}
        {item.workers && item.workers.length > 0 && (
          <ul className="msg-progress-workers" aria-label="Running workers">
            {item.workers.map((w) => (
              <li key={w.id}><strong>{w.description}</strong>{w.line && <span> · {w.line}</span>}</li>
            ))}
          </ul>
        )}
        {item.text && <Markdown text={item.text} />}
      </div>
    );
  }
  if (item.kind === "worker") {
    return (
      <div className="msg msg-agent msg-worker">
        <span className="msg-worker-label">Worker started</span> <strong>{item.text}</strong>
        {item.detail && <span className="msg-worker-type"> · {item.detail}</span>}
      </div>
    );
  }
  if (item.kind === "worker-result") {
    return (
      <details className="msg msg-agent msg-worker-result">
        <summary>Worker result{item.status ? ` (${item.status})` : ""}</summary>
        <Markdown text={item.text} />
      </details>
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
