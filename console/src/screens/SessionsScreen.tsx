import { useEffect, useState } from "react";
import { Alert, Badge, Button, SearchField, Text, Textarea } from "@servicetitan/anvil2";
import * as api from "../api";
import type { HubMessage, HubSessionView } from "../types";
import type { ScreenProps } from "./ScreenProps";

export default function SessionsScreen({ state, onChanged, onOpenInbox }: ScreenProps) {
  const [search, setSearch] = useState("");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [messages, setMessages] = useState<HubMessage[]>([]);
  const [draft, setDraft] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [pausedIds, setPausedIds] = useState<string[]>([]);

  const controllerId = state?.controller?.sessionId ?? null;
  const isController = (s: HubSessionView) => s.id === controllerId || s.tags.includes("fyrd-controller");
  const all = [...(state?.sessions ?? [])].sort((a, b) => Number(isController(b)) - Number(isController(a)));
  const q = search.trim().toLowerCase();
  const sessions = q ? all.filter((s) => s.title.toLowerCase().includes(q)) : all;
  const selected = all.find((s) => s.id === selectedId) ?? null;

  const run = async (fn: () => Promise<unknown>) => {
    setError(null);
    try {
      await fn();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  const loadMessages = (id: string) => run(async () => setMessages(await api.getMessages(id)));

  useEffect(() => {
    setMessages([]);
  }, [selectedId]);

  useEffect(() => {
    if (!selectedId) return;
    let cancelled = false;
    api.getMessages(selectedId).then(
      (m) => { if (!cancelled) setMessages(m ?? []); },
      (e) => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)); },
    );
    return () => { cancelled = true; };
  }, [selectedId, state?.lastPollAt]);

  // Once the server reports the session as idle/archived, it is authoritative again.
  useEffect(() => {
    setPausedIds((ids) => ids.filter((id) => {
      const st = state?.sessions.find((x) => x.id === id)?.status;
      return st !== "idle" && st !== "archived";
    }));
  }, [state?.sessions]);

  const startController = () =>
    run(async () => {
      const info = await api.openController();
      window.open(info.url, "_blank", "noopener");
      await onChanged();
    });

  const question = selected ? state?.questions.find((x) => x.sessionId === selected.id) : undefined;
  const canPause = !!selected && !pausedIds.includes(selected.id) && (selected.status === "running" || selected.status === "requires_action");
  const canResume = !!selected && (pausedIds.includes(selected.id) || selected.status === "idle");

  return (
    <>
      <Text variant="headline" el="h2" size="large">Sessions</Text>
      <Text subdued>{all.length} Claude Code sessions</Text>
      {error && <Alert status="danger" title={error} role="alert" />}
      <div>
        {state && !state.controller && <Button appearance="primary" onClick={startController}>Start Controller</Button>}
        {state?.controller && (
          <Button appearance="secondary" onClick={() => window.open(state.controller!.url, "_blank", "noopener")}>
            Open Controller
          </Button>
        )}
      </div>
      <div className="split">
        <div className="card">
          <SearchField aria-label="Search sessions" placeholder="Search sessions" value={search} onChange={(e) => setSearch(e.target.value)} onClear={() => setSearch("")} />
          {sessions.length === 0 && <Text subdued>No sessions match that search.</Text>}
          <ul style={{ listStyle: "none", margin: 0, padding: 0 }}>
            {sessions.map((s) => (
              <li key={s.id}>
                <button type="button" className="row-btn" aria-current={s.id === selectedId} onClick={() => setSelectedId(s.id)}>
                  <strong>{s.title}</strong> {isController(s) && <Badge>Controller</Badge>}
                  <div><Text size="small" subdued>{s.status} · {s.role}</Text></div>
                </button>
              </li>
            ))}
          </ul>
        </div>
        <div className="card">
          {!selected ? (
            <Text subdued>Select a session to see its details.</Text>
          ) : (
            <>
              <Text variant="headline" el="h3" size="medium">{selected.title}</Text>
              <Text subdued>{selected.status}</Text>
              <div>
                {canResume && (
                  <Button
                    appearance="secondary"
                    onClick={() => run(async () => {
                      await api.resume(selected.id);
                      setPausedIds((ids) => ids.filter((id) => id !== selected.id));
                      await onChanged();
                    })}
                  >
                    Resume session
                  </Button>
                )}
                {canPause && (
                  <Button
                    appearance="secondary"
                    onClick={() => run(async () => {
                      await api.pause(selected.id);
                      setPausedIds((ids) => [...ids, selected.id]);
                      await onChanged();
                    })}
                  >
                    Pause session
                  </Button>
                )}
              </div>
              {question && (
                <Alert status="info" title="This session is waiting for your response.">
                  <Button appearance="secondary" onClick={() => onOpenInbox?.()}>Respond in inbox</Button>
                </Alert>
              )}
              <div style={{ display: "flex", flexDirection: "column", gap: 6 }} aria-label="Messages" role="log">
                {messages.map((m, i) => (
                  <div key={i} className={m.role === "user" ? "msg-user" : "msg-agent"}>{m.text}</div>
                ))}
              </div>
              <Textarea label="Message this session" rows={2} value={draft} onChange={(e) => setDraft(e.target.value)} />
              <div>
                <Button
                  appearance="primary"
                  disabled={draft.trim() === ""}
                  onClick={() => run(async () => {
                    await api.sendMessage(selected.id, draft.trim());
                    setDraft("");
                    await loadMessages(selected.id);
                  })}
                >
                  Send
                </Button>
              </div>
              <Text variant="headline" el="h4" size="small">Estimated cost</Text>
              <Text>—</Text>
              <div style={{ display: "flex", gap: 8 }} role="group" aria-label="Add budget">
                <Button appearance="secondary" disabled>+$5</Button>
                <Button appearance="secondary" disabled>+$25</Button>
                <Button appearance="secondary" disabled>+$100</Button>
              </div>
              <Text variant="headline" el="h4" size="small">Tags</Text>
              <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
                {selected.tags.map((t) => <Badge key={t}>{t}</Badge>)}
              </div>
              <Text variant="headline" el="h4" size="small">Pull requests</Text>
              <Text subdued>—</Text>
              <Text variant="headline" el="h4" size="small">Details</Text>
              <Text size="small">Repository: {selected.repo ?? "—"}</Text>
              <Text size="small">Started: {selected.createdAt ?? "—"}</Text>
              <Text size="small">Last activity: {selected.lastActivity ?? "—"}</Text>
              <Text size="small">Role: {selected.role}</Text>
            </>
          )}
        </div>
      </div>
    </>
  );
}
