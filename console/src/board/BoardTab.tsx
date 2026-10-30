import { useState } from "react";
import * as api from "../api";
import NewSpecForm from "../specs/NewSpecForm";
import type { BoardSpec, FyrdConfig, PeerView, UnitStage } from "../types";
import SettingsPanel from "./SettingsPanel";

export type { BoardSpec };

interface Props {
  specs: BoardSpec[];
  config: FyrdConfig | null;
  onChange: () => void;
  onRelayError?: (message: string) => void;
}

const STAGES: UnitStage[] = ["Implement", "Review", "Validate"];

/** implement · review · validate: ✓ done, ● current, ○ not started. */
export function stageMarkers(stage: UnitStage): string {
  const current = stage === "Done" ? STAGES.length : STAGES.indexOf(stage);
  return STAGES.map((_, i) => (i < current ? "✓" : i === current ? "●" : "○")).join("");
}

/** 52000 -> "52k", 1000000 -> "1M". */
export function formatTokens(n: number): string {
  if (n >= 1_000_000) return `${+(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${Math.round(n / 1_000)}k`;
  return String(n);
}

function PeerLinks({ peers }: { peers: PeerView[] }) {
  return (
    <span className="peers">
      {peers.map((p) => (
        <a key={p.sessionId} href={p.url} target="_blank" rel="noreferrer" title={p.status ?? undefined}>
          {p.role}
        </a>
      ))}
    </span>
  );
}

export default function BoardTab({ specs, config, onChange, onRelayError }: Props) {
  const [creating, setCreating] = useState(false);
  const [settings, setSettings] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [attacking, setAttacking] = useState<string | null>(null);

  async function attack(id: string) {
    if (attacking) return;
    setAttacking(id);
    setErrors((e) => {
      const { [id]: _drop, ...rest } = e;
      return rest;
    });
    try {
      await api.attack(id);
      onChange();
    } catch (e) {
      setErrors((cur) => ({ ...cur, [id]: e instanceof Error ? e.message : String(e) }));
    } finally {
      setAttacking(null);
    }
  }

  return (
    <div className="board">
      <div className="board-bar">
        <button type="button" onClick={() => setCreating(true)}>New spec</button>
        <button type="button" aria-expanded={settings} onClick={() => setSettings((v) => !v)}>Settings</button>
      </div>
      {settings && config && (
        <SettingsPanel
          config={config}
          onSaved={(relayError) => {
            if (relayError) onRelayError?.(relayError);
            onChange();
          }}
        />
      )}
      {creating && (
        <NewSpecForm
          onCancel={() => setCreating(false)}
          onCreated={() => {
            setCreating(false);
            onChange();
          }}
        />
      )}
      {specs.map(({ entry, units, lead, peers }) => {
        if (!entry.run) {
          return (
            <section key={entry.id} className="spec spec-error">
              {entry.id}: {entry.error}
            </section>
          );
        }
        const run = entry.run;
        const showContext = lead && lead.contextUsed != null && lead.contextMax;
        return (
          <section key={entry.id} className="spec">
            <header>
              <h3>{run.title}</h3>
              <span className="repo">{run.repo}</span>
              <span className="status">{run.status}</span>
              {lead && (
                <a className="lead-link" href={lead.url} target="_blank" rel="noreferrer">
                  {`Lead${lead.status ? ` · ${lead.status}` : ""}`}
                </a>
              )}
              {run.branch && <span className="branch">{run.branch}</span>}
              {run.status === "Draft" && (
                <button type="button" disabled={attacking !== null} onClick={() => attack(entry.id)}>
                  Attack
                </button>
              )}
            </header>
            {errors[entry.id] && <p className="error" role="alert">{errors[entry.id]}</p>}
            {run.error && <p className="error">{run.error}</p>}
            {showContext && (
              <div className="context">
                <progress value={lead.contextUsed!} max={lead.contextMax!} aria-label="Lead context usage" />
                <span>{`${formatTokens(lead.contextUsed!)} / ${formatTokens(lead.contextMax!)} tokens`}</span>
              </div>
            )}
            <ul className="units">
              {units.map((u) => (
                <li key={u.id}>
                  <span className="unit-title">{u.title}</span>
                  <span className="markers" title="implement · review · validate">
                    {stageMarkers(u.stage)}
                  </span>
                  {u.state === "WaitingOnOwner" && <span className="waiting">waiting on you</span>}
                  <PeerLinks peers={peers.filter((p) => p.unitId === u.id)} />
                </li>
              ))}
            </ul>
          </section>
        );
      })}
    </div>
  );
}
