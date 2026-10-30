import { useEffect, useState } from "react";
import * as api from "../api";

interface Props {
  cap: number;
  onSaved: () => void;
}

export default function SquadCapControl({ cap, onSaved }: Props) {
  const [value, setValue] = useState(String(cap));
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Follow the server value when it changes (e.g. after a poll), not on every render.
  useEffect(() => setValue(String(cap)), [cap]);

  async function save() {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await api.putConfig({ squadCap: Number(value) });
      onSaved();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="squad-cap">
      <label>
        Squad cap
        <input
          type="number"
          min={1}
          max={8}
          value={value}
          onChange={(e) => setValue(e.target.value)}
        />
      </label>
      <button type="button" disabled={busy} onClick={save}>Save</button>
      {error && <span className="error" role="alert">{error}</span>}
    </div>
  );
}
