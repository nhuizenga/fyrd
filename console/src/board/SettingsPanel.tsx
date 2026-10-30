import { useEffect, useState } from "react";
import * as api from "../api";
import type { FyrdConfig } from "../types";

interface Props {
  config: FyrdConfig;
  onSaved: (relayError?: string) => void;
}

export default function SettingsPanel({ config, onSaved }: Props) {
  const [cap, setCap] = useState(String(config.squadCap));
  const [env, setEnv] = useState(config.environmentId ?? "");
  const [lead, setLead] = useState(config.leadModel);
  const [peer, setPeer] = useState(config.peerModel);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Follow the server values when they change (e.g. after a poll), not on every render.
  useEffect(() => setCap(String(config.squadCap)), [config.squadCap]);
  useEffect(() => setEnv(config.environmentId ?? ""), [config.environmentId]);
  useEffect(() => setLead(config.leadModel), [config.leadModel]);
  useEffect(() => setPeer(config.peerModel), [config.peerModel]);

  async function save() {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const saved = await api.putConfig({
        squadCap: Number(cap),
        environmentId: env.trim() === "" ? null : env.trim(),
        leadModel: lead.trim(),
        peerModel: peer.trim(),
      });
      onSaved(saved?.relayError);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form
      className="settings"
      noValidate
      onSubmit={(e) => {
        e.preventDefault();
        void save();
      }}
    >
      <label>
        Squad cap
        <input type="number" min={1} max={8} value={cap} onChange={(e) => setCap(e.target.value)} />
      </label>
      <label>
        Environment ID
        <input type="text" placeholder="env_…" value={env} onChange={(e) => setEnv(e.target.value)} />
      </label>
      <label>
        Lead model
        <input type="text" value={lead} onChange={(e) => setLead(e.target.value)} />
      </label>
      <label>
        Peer model
        <input type="text" value={peer} onChange={(e) => setPeer(e.target.value)} />
      </label>
      <button type="submit" disabled={busy}>Save</button>
      {error && <span className="error" role="alert">{error}</span>}
    </form>
  );
}
