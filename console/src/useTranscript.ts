import { useEffect, useState } from "react";
import type { TranscriptItem } from "./types";

export interface Transcript {
  items: TranscriptItem[];
  /** The latest live-only progress line ("in progress" status); replaced by each new one, cleared by the next saved item. */
  progress: TranscriptItem | null;
  /** True once the stream has opened at least once for this session. */
  connected: boolean;
  /** The last `event: fault` detail from the server, cleared when new items arrive. */
  fault: string | null;
}

const empty: Transcript = { items: [], progress: null, connected: false, fault: null };

/**
 * Follows a session's live transcript over Server-Sent Events. The server replays the whole transcript first,
 * then streams new items; EventSource reconnects on its own and resumes from the last `id:` it saw.
 */
export function useTranscript(sessionId: string | null): Transcript {
  const [transcript, setTranscript] = useState<Transcript>(empty);

  useEffect(() => {
    setTranscript(empty);
    if (!sessionId) return;
    const source = new EventSource(`/api/hub/sessions/${encodeURIComponent(sessionId)}/stream`);
    const seen = new Set<string>();
    source.onopen = () => setTranscript((t) => ({ ...t, connected: true }));
    source.addEventListener("item", (e) => {
      const item = JSON.parse((e as MessageEvent<string>).data) as TranscriptItem;
      if (seen.has(item.key)) return;
      seen.add(item.key);
      setTranscript((t) => ({ ...t, fault: null, progress: null, items: [...t.items, item] }));
    });
    source.addEventListener("progress", (e) => {
      const progress = JSON.parse((e as MessageEvent<string>).data) as TranscriptItem;
      setTranscript((t) => ({ ...t, progress }));
    });
    source.addEventListener("fault", (e) => {
      const detail = (JSON.parse((e as MessageEvent<string>).data) as { detail?: string }).detail ?? "Stream error";
      setTranscript((t) => ({ ...t, fault: detail }));
    });
    return () => source.close();
  }, [sessionId]);

  return transcript;
}
