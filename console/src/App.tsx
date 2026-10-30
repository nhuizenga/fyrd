import { useCallback, useEffect, useState } from "react";
import * as api from "./api";
import BoardTab, { type BoardSpec } from "./board/BoardTab";
import InboxTab from "./inbox/InboxTab";
import type { FyrdConfig, InboxCard } from "./types";

const POLL_MS = 3000;

export default function App() {
  const [tab, setTab] = useState<"inbox" | "board">("inbox");
  const [cards, setCards] = useState<InboxCard[]>([]);
  const [specs, setSpecs] = useState<BoardSpec[]>([]);
  const [config, setConfig] = useState<FyrdConfig | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      const [inbox, list, cfg] = await Promise.all([api.getInbox(), api.getSpecs(), api.getConfig()]);
      const boardSpecs = await Promise.all(
        list.map(async (entry): Promise<BoardSpec> => {
          if (!entry.run) return { entry, units: [] };
          try {
            return { entry, units: (await api.getSpec(entry.id)).units };
          } catch {
            return { entry, units: [] };
          }
        }),
      );
      setCards(inbox);
      setSpecs(boardSpecs);
      setConfig(cfg);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
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
      {tab === "inbox" ? (
        <InboxTab cards={cards} specs={specs.map((s) => s.entry)} onAnswered={refresh} />
      ) : (
        <BoardTab specs={specs} config={config} onChange={refresh} />
      )}
    </main>
  );
}
