import { useCallback, useEffect, useState } from "react";
import * as api from "./api";
import BoardTab from "./board/BoardTab";
import InboxTab from "./inbox/InboxTab";
import type { Board, FyrdConfig, InboxCard } from "./types";

const POLL_MS = 3000;

export default function App() {
  const [tab, setTab] = useState<"inbox" | "board">("inbox");
  const [cards, setCards] = useState<InboxCard[]>([]);
  const [board, setBoard] = useState<Board | null>(null);
  const [relayError, setRelayError] = useState<string | null>(null);
  const [dismissedError, setDismissedError] = useState<string | null>(null);
  const specs = board?.specs ?? [];
  const [config, setConfig] = useState<FyrdConfig | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    // Each endpoint degrades on its own: a failure keeps that tab's last data instead of freezing the rest.
    const [inbox, bd, cfg] = await Promise.allSettled([api.getInbox(), api.getBoard(), api.getConfig()]);
    if (inbox.status === "fulfilled") setCards(inbox.value);
    if (cfg.status === "fulfilled") setConfig(cfg.value);
    if (bd.status === "fulfilled") setBoard(bd.value);
    const failures = [inbox, bd, cfg].filter((r): r is PromiseRejectedResult => r.status === "rejected");
    if (failures.length === 3) {
      const reason = failures[0].reason;
      setError(reason instanceof Error ? reason.message : String(reason));
    } else {
      setError(null);
    }
  }, []);

  const onRelayError = useCallback((message?: string) => {
    if (message) setRelayError(message);
  }, []);

  useEffect(() => {
    void refresh();
    const tick = () => {
      if (document.visibilityState === "visible") void refresh();
    };
    const timer = setInterval(tick, POLL_MS);
    document.addEventListener("visibilitychange", tick);
    return () => {
      clearInterval(timer);
      document.removeEventListener("visibilitychange", tick);
    };
  }, [refresh]);

  const units = specs.flatMap((s) => s.units);
  const done = units.filter((u) => u.state === "Done").length;
  const boardLabel = units.length > 0 ? `Board · ${done}/${units.length} done` : "Board";

  const lastError = board?.lastError && board.lastError !== dismissedError ? board.lastError : null;
  const amber = relayError ?? lastError;

  return (
    <main>
      <h1>Fyrd</h1>
      <div role="tablist" className="tabs">
        <button role="tab" aria-selected={tab === "inbox"} onClick={() => setTab("inbox")}>
          {`Inbox (${cards.length})`}
        </button>
        <button role="tab" aria-selected={tab === "board"} onClick={() => setTab("board")}>
          {boardLabel}
        </button>
      </div>
      {error && <p className="error" role="alert">Can't reach the Fyrd server: {error}</p>}
      {board?.signedOut && (
        <div className="banner banner-signedout" role="alert">
          <span>Signed out of Claude — run <code>claude auth login</code></span>
        </div>
      )}
      {amber && (
        <div className="banner" role="alert">
          <span>{amber}</span>
          <button type="button" aria-label="Dismiss" onClick={() => (relayError ? setRelayError(null) : setDismissedError(amber))}>
            Dismiss
          </button>
        </div>
      )}
      {board?.rateLimit && (
        <div className="chips">
          <span className="chip">
            {`Rate limit: ${board.rateLimit.status ?? "unknown"}${board.rateLimit.resetsAt ? ` · resets ${new Date(board.rateLimit.resetsAt).toLocaleString()}` : ""}`}
          </span>
        </div>
      )}
      {tab === "inbox" ? (
        <InboxTab cards={cards} specs={specs.map((s) => s.entry)} onAnswered={(r) => {
          onRelayError(r);
          void refresh();
        }} />
      ) : (
        <BoardTab specs={specs} config={config} onChange={refresh} onRelayError={onRelayError} />
      )}
    </main>
  );
}
