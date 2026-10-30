import { useState } from "react";
import * as api from "../api";
import NewSpecForm from "../specs/NewSpecForm";
import type { FyrdConfig, SpecListEntry, Unit, UnitStage } from "../types";
import SquadCapControl from "./SquadCapControl";

export interface BoardSpec {
  entry: SpecListEntry;
  units: Unit[];
}

interface Props {
  specs: BoardSpec[];
  config: FyrdConfig | null;
  onChange: () => void;
}

const STAGES: UnitStage[] = ["Implement", "Review", "Validate"];

/** implement · review · validate: ✓ done, ● current, ○ not started. */
export function stageMarkers(stage: UnitStage): string {
  const current = stage === "Done" ? STAGES.length : STAGES.indexOf(stage);
  return STAGES.map((_, i) => (i < current ? "✓" : i === current ? "●" : "○")).join("");
}

export default function BoardTab({ specs, config, onChange }: Props) {
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [attacking, setAttacking] = useState<string | null>(null);

  async function attack(id: string) {
    if (attacking) return;
    setAttacking(id);
    setError(null);
    try {
      await api.attack(id);
      onChange();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setAttacking(null);
    }
  }

  return (
    <div className="board">
      <div className="board-bar">
        <button type="button" onClick={() => setCreating(true)}>New spec</button>
        {config && <SquadCapControl cap={config.squadCap} onSaved={onChange} />}
      </div>
      {error && <p className="error" role="alert">{error}</p>}
      {creating && (
        <NewSpecForm
          onCancel={() => setCreating(false)}
          onCreated={() => {
            setCreating(false);
            onChange();
          }}
        />
      )}
      {specs.map(({ entry, units }) =>
        entry.run ? (
          <section key={entry.id} className="spec">
            <header>
              <h3>{entry.run.title}</h3>
              <span className="repo">{entry.run.repo}</span>
              <span className="status">{entry.run.status}</span>
              {entry.run.status === "Draft" && (
                <button type="button" disabled={attacking !== null} onClick={() => attack(entry.id)}>
                  Attack
                </button>
              )}
            </header>
            <ul className="units">
              {units.map((u) => (
                <li key={u.id}>
                  <span className="unit-title">{u.title}</span>
                  <span className="markers" title="implement · review · validate">
                    {stageMarkers(u.stage)}
                  </span>
                  {u.state === "WaitingOnOwner" && <span className="waiting">waiting on you</span>}
                </li>
              ))}
            </ul>
          </section>
        ) : (
          <section key={entry.id} className="spec spec-error">
            {entry.id}: {entry.error}
          </section>
        ),
      )}
    </div>
  );
}
