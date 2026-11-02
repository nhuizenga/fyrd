import { useCallback, useEffect, useState } from "react";
import type { TranscriptItem } from "./types";

/** How many of the newest items are rendered at first, unless the owner changed it in Settings; "load more" widens the window by this much. */
export const DEFAULT_ITEM_LIMIT = 100;
export const MIN_ITEM_LIMIT = 10;
export const MAX_ITEM_LIMIT = 5000;
const LIMIT_KEY = "fyrd.transcriptItemLimit";

/** The configured render limit (a per-browser setting), or the default when unset or invalid. */
export function getItemLimit(): number {
  try {
    const n = Number(window.localStorage.getItem(LIMIT_KEY));
    if (Number.isInteger(n) && n >= MIN_ITEM_LIMIT && n <= MAX_ITEM_LIMIT) return n;
  } catch { /* storage unavailable: use the default */ }
  return DEFAULT_ITEM_LIMIT;
}

/** Saves the render limit; takes effect the next time a session is selected. */
export function setItemLimit(n: number) {
  try {
    if (n === DEFAULT_ITEM_LIMIT) window.localStorage.removeItem(LIMIT_KEY);
    else window.localStorage.setItem(LIMIT_KEY, String(n));
  } catch { /* storage unavailable: the setting just won't stick */ }
}
/** Sessions whose loaded conversations are kept for instant re-selection; the least recently used is dropped past this. */
const MAX_CACHED_SESSIONS = 20;

export interface Transcript {
  /** The newest items, capped at the current limit. */
  items: TranscriptItem[];
  /** How many older items are loaded but not rendered. */
  hiddenCount: number;
  /** Renders another page of older items. */
  loadMore: () => void;
  /** The latest live-only progress line ("in progress" status); replaced by each new one, cleared by the next saved item. */
  progress: TranscriptItem | null;
  /** True once the stream has opened at least once for this session. */
  connected: boolean;
  /** The last `event: fault` detail from the server, cleared when new items arrive. */
  fault: string | null;
}

/** Progress is live-only (the server never replays it), so keep each session's last line for when the owner comes back. */
const lastProgress = new Map<string, TranscriptItem>();

/** Loaded conversations by session (Map order = recency), so re-selecting a session doesn't start from a blank thread. */
const cache = new Map<string, TranscriptItem[]>();

function remember(sessionId: string, items: TranscriptItem[]) {
  cache.delete(sessionId);
  cache.set(sessionId, items);
  while (cache.size > MAX_CACHED_SESSIONS) cache.delete(cache.keys().next().value as string);
}

/** Test hook: forget every cached conversation and progress line. */
export function clearTranscriptCache() {
  cache.clear();
  lastProgress.clear();
}

type State = Omit<Transcript, "hiddenCount" | "loadMore">;
const empty: State = { items: [], progress: null, connected: false, fault: null };

/**
 * Follows a session's live transcript over Server-Sent Events. The server replays the whole transcript first,
 * then streams new items; EventSource reconnects on its own and resumes from the last `id:` it saw.
 */
export function useTranscript(sessionId: string | null): Transcript {
  const [transcript, setTranscript] = useState<State>(empty);
  const [limit, setLimit] = useState(getItemLimit);
  const loadMore = useCallback(() => setLimit((l) => l + getItemLimit()), []);

  useEffect(() => {
    setLimit(getItemLimit());
    const cached = sessionId ? cache.get(sessionId) ?? [] : [];
    setTranscript(sessionId ? { ...empty, items: cached, progress: lastProgress.get(sessionId) ?? null } : empty);
    if (!sessionId) return;
    // With a cached conversation, ask the server to resume after its newest event instead of replaying everything.
    const after = cached.reduce((max, i) => Math.max(max, i.seq), -1);
    const resume = cached.length ? `?after=${after}` : "";
    const source = new EventSource(`/api/hub/sessions/${encodeURIComponent(sessionId)}/stream${resume}`);
    // Defensive: anything already cached is skipped, not re-added.
    const seen = new Set<string>(cached.map((i) => i.key));
    let loaded = cached;
    source.onopen = () => setTranscript((t) => ({ ...t, connected: true }));
    source.addEventListener("item", (e) => {
      const item = JSON.parse((e as MessageEvent<string>).data) as TranscriptItem;
      if (seen.has(item.key)) return;
      seen.add(item.key);
      lastProgress.delete(sessionId);
      loaded = [...loaded, item];
      remember(sessionId, loaded);
      setTranscript((t) => ({ ...t, fault: null, progress: null, items: loaded }));
    });
    source.addEventListener("progress", (e) => {
      const progress = JSON.parse((e as MessageEvent<string>).data) as TranscriptItem;
      // An empty frame means the turn moved on (or ended): nothing is in progress any more.
      const idle = !progress.status && !progress.text;
      if (idle) lastProgress.delete(sessionId);
      else lastProgress.set(sessionId, progress);
      setTranscript((t) => ({ ...t, progress: idle ? null : progress }));
    });
    source.addEventListener("fault", (e) => {
      const detail = (JSON.parse((e as MessageEvent<string>).data) as { detail?: string }).detail ?? "Stream error";
      setTranscript((t) => ({ ...t, fault: detail }));
    });
    return () => source.close();
  }, [sessionId]);

  const hiddenCount = Math.max(0, transcript.items.length - limit);
  const items = hiddenCount ? transcript.items.slice(hiddenCount) : transcript.items;
  return { ...transcript, items, hiddenCount, loadMore };
}
