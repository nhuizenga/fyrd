import { useCallback, useEffect, useState } from "react";
import { Alert, Button, SelectFieldSync, Text, TextField, Textarea } from "@servicetitan/anvil2";
import * as api from "../api";
import type { HubMemory, HubPersona, MemoryEntry, MemoryScopeView } from "../types";
import type { ScreenProps } from "./ScreenProps";

const MODELS = [
  { id: "claude-opus-5-5", label: "Opus 5.5" },
  { id: "claude-sonnet-5-5", label: "Sonnet 5.5" },
  { id: "claude-haiku-5-5", label: "Haiku 5.5" },
  { id: "claude-fable-5-1", label: "Fable 5.1" },
];
const MODEL_OPTIONS = MODELS.map((m) => ({ id: m.id, label: `${m.label} · ${m.id}` }));
const APPLY_NOTE = "Changes apply to sessions created from now on.";
const slug = (s: string) => s.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 40).replace(/-+$/g, "");
const errText = (e: unknown) => (e instanceof Error ? e.message : String(e));

function EnvironmentCard() {
  const [env, setEnv] = useState("");
  const [cap, setCap] = useState("20");
  const [capError, setCapError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);

  useEffect(() => {
    api.getConfig().then((c) => {
      if (!c) return;
      setEnv(c.environmentId ?? "");
      setCap(String(c.sessionCap));
    }).catch((e) => setError(errText(e)));
  }, []);

  const save = async (patch: Parameters<typeof api.updateConfig>[0], what: string) => {
    setError(null);
    setSaved(null);
    try {
      await api.updateConfig(patch);
      setSaved(`${what} saved.`);
    } catch (e) {
      setError(errText(e));
    }
  };

  const saveCap = () => {
    const n = Number(cap);
    if (!/^\d+$/.test(cap.trim()) || n < 1 || n > 100) {
      setCapError("Enter a whole number from 1 to 100.");
      return;
    }
    setCapError(null);
    void save({ sessionCap: n }, "Session cap");
  };

  return (
    <section className="card" aria-labelledby="env-h">
      <Text variant="headline" el="h3" size="medium" id="env-h">Environment</Text>
      <Text subdued>Where sessions run and how this fyrd is identified.</Text>
      {error && <Alert status="danger" title={error} role="alert" />}
      {saved && <Text role="status">{saved}</Text>}
      <TextField
        label="Cloud environment name"
        description="The environment new sessions start in. Running sessions keep their current environment."
        value={env}
        onChange={(e) => setEnv(e.target.value)}
      />
      <div>
        <Button appearance="primary" aria-label="Save environment" onClick={() => save({ environmentId: env.trim() || null }, "Environment")}>
          Save
        </Button>
      </div>
      <TextField
        label="Session cap"
        inputMode="numeric"
        value={cap}
        error={capError ?? undefined}
        onChange={(e) => setCap(e.target.value)}
      />
      <div>
        <Button appearance="primary" aria-label="Save session cap" onClick={saveCap}>Save</Button>
      </div>
      <Text size="small" subdued>{APPLY_NOTE}</Text>
    </section>
  );
}

function PersonasCard() {
  const [personas, setPersonas] = useState<HubPersona[]>([]);
  const [editing, setEditing] = useState<{ draft: HubPersona; isNew: boolean } | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setPersonas((await api.getPersonas()) ?? []);
    } catch (e) {
      setError(errText(e));
    }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const d = editing?.draft;
  const locked = !!d && !editing!.isNew && (d.locked || d.id === "controller");
  const patch = (p: Partial<HubPersona>) => setEditing((e) => (e ? { ...e, draft: { ...e.draft, ...p } } : e));

  const save = async () => {
    if (!d) return;
    setError(null);
    const id = editing!.isNew ? slug(d.name) : d.id;
    if (!id || !d.name.trim()) {
      setError("Give the persona a name.");
      return;
    }
    if (editing!.isNew && (id === "shared" || personas.some((p) => p.id === id))) {
      setError("A persona with this name already exists.");
      return;
    }
    try {
      await api.savePersona({ ...d, id, name: d.name.trim() });
      setEditing(null);
      await load();
    } catch (e) {
      setError(errText(e));
    }
  };

  const remove = async () => {
    if (!d) return;
    setError(null);
    try {
      await api.deletePersona(d.id);
      setEditing(null);
      await load();
    } catch (e) {
      setError(errText(e));
    }
  };

  return (
    <section className="card" aria-labelledby="personas-h">
      <Text variant="headline" el="h3" size="medium" id="personas-h">Personas</Text>
      {error && <Alert status="danger" title={error} role="alert" />}
      <div>
        <Button
          appearance="secondary"
          onClick={() => setEditing({ isNew: true, draft: { id: "", name: "", model: "claude-sonnet-5-5", definition: "", locked: false } })}
        >
          Add persona
        </Button>
      </div>
      <div className="split">
        <ul style={{ listStyle: "none", margin: 0, padding: 0 }}>
          {personas.map((p) => (
            <li key={p.id}>
              <button
                type="button"
                className="row-btn"
                aria-current={!!d && !editing!.isNew && d.id === p.id}
                onClick={() => setEditing({ isNew: false, draft: { ...p } })}
              >
                {p.name}
              </button>
            </li>
          ))}
        </ul>
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          {!d ? (
            <Text subdued>Select a persona to edit it.</Text>
          ) : (
            <>
              <TextField label="Name" value={d.name} disabled={!editing!.isNew} onChange={(e) => patch({ name: e.target.value })} />
              <SelectFieldSync
                label="Claude model"
                disableSearch
                disableClearButton
                options={MODEL_OPTIONS}
                value={MODEL_OPTIONS.find((m) => m.id === d.model) ?? null}
                onSelectedOptionChange={(o) => o && patch({ model: String(o.id) })}
              />
              <Textarea
                label="Definition"
                description="Instructions given to the agent at the start of every session that uses this persona."
                rows={8}
                value={d.definition}
                onChange={(e) => patch({ definition: e.target.value })}
              />
              <Text size="small" subdued>Instructions given to the agent at the start of every session that uses this persona.</Text>
              <Text size="small" subdued>{APPLY_NOTE}</Text>
              <div style={{ display: "flex", gap: 8 }}>
                <Button appearance="primary" onClick={save}>Save persona</Button>
                <Button appearance="ghost" onClick={() => setEditing(null)}>Discard</Button>
                {!editing!.isNew && (
                  <Button appearance="secondary" disabled={locked} onClick={remove}>Delete persona</Button>
                )}
              </div>
              {locked && <Text size="small">The Controller persona can't be deleted</Text>}
            </>
          )}
        </div>
      </div>
    </section>
  );
}

function ScopeGroup({ scope, onChanged, setError }: { scope: MemoryScopeView; onChanged: () => Promise<void>; setError: (s: string | null) => void }) {
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editText, setEditText] = useState("");
  const [confirmId, setConfirmId] = useState<string | null>(null);

  const attempt = async (fn: () => Promise<unknown>) => {
    setError(null);
    try {
      await fn();
      setEditingId(null);
      setConfirmId(null);
      await onChanged();
    } catch (e) {
      setError(errText(e));
    }
  };

  const row = (e: MemoryEntry) => (
    <li key={e.id} style={{ display: "flex", flexDirection: "column", gap: 6, padding: "6px 0" }}>
      {editingId === e.id ? (
        <>
          <Textarea label="Edit memory entry" rows={2} value={editText} onChange={(ev) => setEditText(ev.target.value)} />
          <div style={{ display: "flex", gap: 8 }}>
            <Button appearance="primary" onClick={() => attempt(() => api.editMemory(scope.scope, e.id, editText.trim()))}>Save</Button>
            <Button appearance="ghost" onClick={() => setEditingId(null)}>Cancel</Button>
          </div>
        </>
      ) : (
        <>
          <Text>{e.text}</Text>
          {confirmId === e.id ? (
            <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
              <Text size="small">Delete this entry? Agents will stop seeing it.</Text>
              <Button appearance="secondary" aria-label="Confirm delete" onClick={() => attempt(() => api.deleteMemory(scope.scope, e.id))}>Delete</Button>
              <Button appearance="ghost" onClick={() => setConfirmId(null)}>Cancel</Button>
            </div>
          ) : (
            <div style={{ display: "flex", gap: 8 }}>
              <Button appearance="ghost" onClick={() => { setEditingId(e.id); setEditText(e.text); }}>Edit</Button>
              <Button appearance="ghost" onClick={() => setConfirmId(e.id)}>Delete</Button>
            </div>
          )}
        </>
      )}
    </li>
  );

  return (
    <div>
      <Text variant="headline" el="h4" size="small">{scope.label}</Text>
      <ul style={{ listStyle: "none", margin: 0, padding: 0 }}>{scope.entries.map(row)}</ul>
    </div>
  );
}

function MemoryCard() {
  const [memory, setMemory] = useState<HubMemory>({ scopes: [] });
  const [scope, setScope] = useState("shared");
  const [text, setText] = useState("");
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setMemory((await api.getHubMemory()) ?? { scopes: [] });
    } catch (e) {
      setError(errText(e));
    }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const scopes = [...memory.scopes].sort((a, b) => Number(b.scope === "shared") - Number(a.scope === "shared"));
  const scopeOptions = (scopes.length ? scopes : [{ scope: "shared", label: "Shared", entries: [] }]).map((s) => ({ id: s.scope, label: s.label }));
  const total = scopes.reduce((n, s) => n + s.entries.length, 0);

  const add = async () => {
    if (!text.trim()) return;
    setError(null);
    try {
      await api.addMemory(scope, text.trim());
      setText("");
      await load();
    } catch (e) {
      setError(errText(e));
    }
  };

  return (
    <section className="card" aria-labelledby="memory-h">
      <Text variant="headline" el="h3" size="medium" id="memory-h">Agent memory</Text>
      {error && <Alert status="danger" title={error} role="alert" />}
      <SelectFieldSync
        label="Memory scope"
        disableSearch
        disableClearButton
        options={scopeOptions}
        value={scopeOptions.find((o) => o.id === scope) ?? null}
        onSelectedOptionChange={(o) => o && setScope(String(o.id))}
      />
      <Textarea label="New memory entry" rows={2} value={text} onChange={(e) => setText(e.target.value)} />
      <div><Button appearance="primary" onClick={add}>Add entry</Button></div>
      <Text size="small" subdued>{APPLY_NOTE}</Text>
      {total === 0 ? (
        <Text subdued>No entries yet. Agents add entries as they work, or add one above.</Text>
      ) : (
        scopes.filter((s) => s.entries.length > 0).map((s) => <ScopeGroup key={s.scope} scope={s} onChanged={load} setError={setError} />)
      )}
    </section>
  );
}

export default function SettingsScreen(_props: ScreenProps) {
  return (
    <>
      <Text variant="headline" el="h2" size="large">Settings</Text>
      <Text subdued>Applies to all sessions in this fyrd</Text>
      <EnvironmentCard />
      <PersonasCard />
      <MemoryCard />
    </>
  );
}
