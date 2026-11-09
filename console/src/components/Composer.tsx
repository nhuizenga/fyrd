import { memo, useEffect, useRef, useState } from "react";
import { Button, Icon, Textarea } from "@servicetitan/anvil2";
import EnterIcon from "@servicetitan/anvil2/assets/icons/material/round/keyboard_return.svg?react";
import ShiftIcon from "@servicetitan/anvil2/assets/icons/material/round/arrow_upward.svg?react";
import { AttachButton, AttachmentChips, attachmentHandlers, useAttachments } from "./AttachmentPicker";

export const DRAFT_KEY = (id: string) => `fyrd.draft.${id}`;

export const readStored = (key: string): string | null => {
  try { return localStorage.getItem(key); } catch { return null; }
};
export const writeStored = (key: string, value: string) => {
  try {
    if (value === "") localStorage.removeItem(key); else localStorage.setItem(key, value);
  } catch { /* storage unavailable: the draft just won't outlive the page */ }
};

/**
 * The message box. It owns the draft text so that each keystroke re-renders only this component, never the
 * transcript above it (which can hold thousands of markdown items). Mount it with `key={sessionId}`: every
 * session keeps its own unsent text (in localStorage too).
 *
 * Sending clears the box at once; while `sending` the box is read-only. `onSend` resolves true once the message is
 * accepted. When it resolves false the owner's text is back in storage, and the box picks it up again.
 *
 * Files can be attached with the button, by dropping them on the box or by pasting; they upload at once and go with
 * the message as `fileIds` (the text may then be empty). Attachments belong to this mount and are not kept in storage.
 */
function Composer({ sessionId, sending, blocked, hint, height, onSend }: {
  sessionId: string;
  sending: boolean;
  blocked: string | null;
  hint?: string;
  height: number;
  onSend: (text: string, fileIds: string[], fileNames: string[]) => Promise<boolean>;
}) {
  const [draft, setDraftState] = useState(() => readStored(DRAFT_KEY(sessionId)) ?? "");
  const setDraft = (value: string) => {
    setDraftState(value);
    writeStored(DRAFT_KEY(sessionId), value);
  };
  const att = useAttachments();
  const canSend = !sending && !blocked && !att.uploading && (draft.trim() !== "" || att.ids.length > 0);

  const [failed, setFailed] = useState(false);
  const boxRef = useRef<HTMLTextAreaElement>(null);
  const wasSending = useRef(false);

  // When the lock lifts, give the owner their cursor back unless they have moved focus somewhere deliberate
  // (clicking Send leaves focus on a button that was just disabled, which drops it to <body>).
  useEffect(() => {
    if (wasSending.current && !sending) {
      const active = document.activeElement;
      if (!active || active === document.body || active.tagName === "BUTTON") boxRef.current?.focus();
    }
    wasSending.current = sending;
  }, [sending]);

  const send = async () => {
    if (!canSend) return;
    const text = draft.trim();
    setFailed(false);
    setDraft("");
    const ok = await onSend(text, att.ids, att.names);
    if (ok) att.clear();
    if (!ok) {
      // Restore from memory, not storage (which may be blocked or full), unless the owner has typed something new.
      setFailed(true);
      setDraftState((cur) => {
        if (cur !== "") return cur;
        writeStored(DRAFT_KEY(sessionId), text);
        return text;
      });
    }
  };

  return (
    <div className="composer" {...attachmentHandlers(att, sending)}>
      <div className="composer-input" style={{ height }}>
        <Textarea
          ref={boxRef}
          label="Message this session"
          hint={hint}
          value={draft}
          readOnly={sending}
          aria-busy={sending}
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
              e.preventDefault();
              void send();
            }
          }}
        />
      </div>
      <AttachmentChips att={att} disabled={sending} />
      <span className="sr-only" aria-live="polite">{sending ? "Sending…" : failed ? "Message not sent" : ""}</span>
      <div className="composer-actions">
        <AttachButton att={att} disabled={sending} />
        <span className="composer-hints">
          <span className="composer-hint" role="img" aria-label="Enter to send" title="Enter to send">
            <Icon svg={EnterIcon} size="small" color="var(--a2-foreground-color-subdued)" />
            <span className="composer-hint-label">Send</span>
          </span>
          <span className="composer-hint" role="img" aria-label="Shift+Enter for a new line" title="Shift+Enter for a new line">
            <Icon svg={ShiftIcon} size="small" color="var(--a2-foreground-color-subdued)" />
            <Icon svg={EnterIcon} size="small" color="var(--a2-foreground-color-subdued)" />
            <span className="composer-hint-label">New line</span>
          </span>
        </span>
        <Button appearance="primary" disabled={!canSend} aria-describedby={blocked ? "budget-block" : undefined} onClick={() => void send()}>Send</Button>
      </div>
    </div>
  );
}

export default memo(Composer);
