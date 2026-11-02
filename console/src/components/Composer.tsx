import { memo, useState } from "react";
import { Button, Icon, Textarea } from "@servicetitan/anvil2";
import EnterIcon from "@servicetitan/anvil2/assets/icons/material/round/keyboard_return.svg?react";
import ShiftIcon from "@servicetitan/anvil2/assets/icons/material/round/arrow_upward.svg?react";

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
 */
function Composer({ sessionId, sending, blocked, hint, height, onSend }: {
  sessionId: string;
  sending: boolean;
  blocked: string | null;
  hint?: string;
  height: number;
  onSend: (text: string) => Promise<boolean>;
}) {
  const [draft, setDraftState] = useState(() => readStored(DRAFT_KEY(sessionId)) ?? "");
  const setDraft = (value: string) => {
    setDraftState(value);
    writeStored(DRAFT_KEY(sessionId), value);
  };
  const canSend = !sending && !blocked && draft.trim() !== "";

  const send = async () => {
    if (!canSend) return;
    const text = draft.trim();
    setDraft("");
    await onSend(text);
    // On failure the screen has put the text back in storage; on success the stored draft is empty.
    setDraftState(readStored(DRAFT_KEY(sessionId)) ?? "");
  };

  return (
    <div className="composer">
      <div className="composer-input" style={{ height }}>
        <Textarea
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
      <div className="composer-actions">
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
