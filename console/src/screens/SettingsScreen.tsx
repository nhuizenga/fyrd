import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Chip, SelectFieldSync, Text, TextField, Textarea } from "@servicetitan/anvil2";
import DeleteIcon from "@servicetitan/anvil2/assets/icons/material/round/delete.svg?react";
import EditIcon from "@servicetitan/anvil2/assets/icons/material/round/edit.svg?react";
import * as api from "../api";
import { navigate } from "../route";
import { relativeTime } from "../time";
import type { HubBudgetView, HubMemory, HubPersona, MemoryEntry, MemoryScopeView, PendingMemoryEntry } from "../types";
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

/** A whole number from `min` to `max` typed in a field, or null. */
const parseWholeNumber = (v: string, min: number, max: number) => {
  const t = v.trim();
  const n = Number(t);
  return /^\d+$/.test(t) && n >= min && n <= max ? n : null;
};
const rangeMessage = (min: number, max: number) => `Enter a whole number from ${min} to ${max}.`;

function EnvironmentCard(_: CardProps) {
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
    const n = parseWholeNumber(cap, 1, 100);
    if (n === null) {
      setCapError(rangeMessage(1, 100));
      return;
    }
    setCapError(null);
    setCap(String(n));
    void save({ sessionCap: n }, "Session cap");
  };

  return (
    <section className="card" aria-labelledby="env-h">
      <Text variant="headline" el="h3" size="medium" id="env-h">Environment</Text>
      <Text subdued>Where sessions run and how this fyrd is identified.</Text>
      {error && <Alert status="danger" title={error} role="alert" />}
      {saved && <Text role="status">{saved}</Text>}
      <TextField
        label="Cloud environment id"
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

/** What the Settings screen hands every section card. */
type CardProps = { budget?: HubBudgetView | null; pendingMemory?: number; onChanged?: () => void | Promise<void> };

const usd = (n: number) => `$${n.toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

/** Today's estimated spend across the whole fyrd (the same total the Fyrd-wide limit is checked against). */
function TotalSpend({ budget }: { budget: HubBudgetView | null | undefined }) {
  return (
    <div className="spend-total" role="status" aria-label="Total spend today">
      {!budget ? (
        <Text subdued>No spend data yet.</Text>
      ) : (
        <>
          <Text size="large">
            <strong>{usd(budget.fyrdCostToday)}</strong> of {usd(budget.fyrdLimitUsd).replace(/\.00$/, "")}
          </Text>
          <Text size="small" subdued>
            {budget.fyrdLimitUsd > 0 ? `${Math.round((budget.fyrdCostToday / budget.fyrdLimitUsd) * 100)}% of today's limit` : ""}
            {" · "}{budget.date} ({budget.timeZone})
          </Text>
          {budget.fyrdBlocked && <Chip label="Limit reached" size="small" />}
        </>
      )}
    </div>
  );
}

function SpendLimitsCard({ budget }: CardProps) {
  const [sessionLimit, setSessionLimit] = useState("50");
  const [fyrdLimit, setFyrdLimit] = useState("500");
  const [sessionError, setSessionError] = useState<string | null>(null);
  const [fyrdError, setFyrdError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [timeZone, setTimeZone] = useState<string | null>(null);

  useEffect(() => {
    api.getConfig().then((c) => {
      if (!c) return;
      setTimeZone(c.timeZone);
      setSessionLimit(String(c.sessionDailyLimitUsd));
      setFyrdLimit(String(c.fyrdDailyLimitUsd));
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

  return (
    <section className="card" aria-labelledby="limits-h">
      <Text variant="headline" el="h3" size="medium" id="limits-h">Spend limits</Text>
      <Text variant="headline" el="h4" size="small">Total spend today</Text>
      <TotalSpend budget={budget} />
      <Text subdued>Daily limits on estimated (list-price) spend. A day runs from midnight in {timeZone ?? "your browser's time zone"}; the last browser to load the console sets it.</Text>
      {error && <Alert status="danger" title={error} role="alert" />}
      {saved && <Text role="status">{saved}</Text>}
      <TextField
        label="Per-session daily limit (USD)"
        description="Whole dollars, 1 to 10000."
        inputMode="numeric"
        value={sessionLimit}
        error={sessionError ?? undefined}
        onChange={(e) => setSessionLimit(e.target.value)}
      />
      <div>
        <Button
          appearance="primary"
          aria-label="Save session limit"
          onClick={() => {
            const n = parseWholeNumber(sessionLimit, 1, 10000);
            if (n === null) return setSessionError(rangeMessage(1, 10000));
            setSessionError(null);
            setSessionLimit(String(n));
            void save({ sessionDailyLimitUsd: n }, "Session limit");
          }}
        >
          Save
        </Button>
      </div>
      <TextField
        label="Fyrd-wide daily limit (USD)"
        description="Whole dollars, 1 to 10000."
        inputMode="numeric"
        value={fyrdLimit}
        error={fyrdError ?? undefined}
        onChange={(e) => setFyrdLimit(e.target.value)}
      />
      <div>
        <Button
          appearance="primary"
          aria-label="Save fyrd limit"
          onClick={() => {
            const n = parseWholeNumber(fyrdLimit, 1, 10000);
            if (n === null) return setFyrdError(rangeMessage(1, 10000));
            setFyrdError(null);
            setFyrdLimit(String(n));
            void save({ fyrdDailyLimitUsd: n }, "Fyrd limit");
          }}
        >
          Save
        </Button>
      </div>
      <Text size="small" subdued>The Controller is never interrupted or blocked by these limits.</Text>
    </section>
  );
}

function PersonasCard(_: CardProps) {
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
    const taken = (p: HubPersona) => p.id !== d.id && (p.id === slug(d.name) || p.name.trim().toLowerCase() === d.name.trim().toLowerCase());
    if ((editing!.isNew && (id === "shared" || personas.some((p) => p.id === id))) || (!editing!.isNew && personas.some(taken))) {
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

  const reset = async () => {
    if (!d) return;
    setError(null);
    try {
      const fresh = await api.resetPersona(d.id);
      setEditing({ isNew: false, draft: { ...fresh } });
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
                {p.updateAvailable && <Chip label="Update available" size="small" />}
              </button>
            </li>
          ))}
        </ul>
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          {!d ? (
            <Text subdued>Select a persona to edit it.</Text>
          ) : (
            <>
              <TextField
                label="Name"
                value={d.name}
                disabled={locked}
                hint={locked ? "The Controller persona can't be renamed." : undefined}
                onChange={(e) => patch({ name: e.target.value })}
              />
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
              {!editing!.isNew && d.updateAvailable && (
                <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
                  <Text size="small">A newer default is available for this persona.</Text>
                  <Button appearance="secondary" onClick={reset}>Reset to default</Button>
                </div>
              )}
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

  const attempt = async (fn: () => Promise<unknown>) => {
    setError(null);
    try {
      await fn();
      setEditingId(null);
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
          <div className="memory-row">
            <Text>{e.text}</Text>
            <Button appearance="ghost" size="small" icon={EditIcon} aria-label="Edit" onClick={() => { setEditingId(e.id); setEditText(e.text); }} />
            <Button appearance="ghost" size="small" icon={DeleteIcon} aria-label="Delete" onClick={() => attempt(() => api.deleteMemory(scope.scope, e.id))} />
          </div>
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

function PendingMemoryGroup({ entries, onChanged, setError }: { entries: PendingMemoryEntry[]; onChanged: () => Promise<void>; setError: (s: string | null) => void }) {
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editText, setEditText] = useState("");
  const [busy, setBusy] = useState(false);

  // One request at a time: a second click would hit an entry that is already gone and show a 404.
  const attempt = async (fn: () => Promise<unknown>) => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await fn();
      setEditingId(null);
      await onChanged();
    } catch (e) {
      setError(errText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div>
      <Text variant="headline" el="h4" size="small">Waiting for your approval</Text>
      <Text size="small" subdued>Agents proposed these for shared memory. Other sessions see them only after you approve.</Text>
      <ul style={{ listStyle: "none", margin: 0, padding: 0 }}>
        {entries.map((e) => (
          <li key={e.id} style={{ display: "flex", flexDirection: "column", gap: 6, padding: "6px 0" }}>
            {editingId === e.id ? (
              <Textarea label="Edit proposed memory" rows={2} value={editText} onChange={(ev) => setEditText(ev.target.value)} />
            ) : (
              <Text>{e.text}</Text>
            )}
            <Text size="small" subdued>From {e.source} · {relativeTime(e.at)}</Text>
            <div style={{ display: "flex", gap: 8 }}>
              {editingId === e.id ? (
                <>
                  <Button appearance="primary" size="small" disabled={busy} onClick={() => attempt(() => api.approvePendingMemory(e.id, editText.trim()))}>Save &amp; approve</Button>
                  <Button appearance="ghost" size="small" disabled={busy} onClick={() => setEditingId(null)}>Cancel</Button>
                </>
              ) : (
                <>
                  <Button appearance="primary" size="small" disabled={busy} onClick={() => attempt(() => api.approvePendingMemory(e.id))}>Approve</Button>
                  <Button appearance="secondary" size="small" disabled={busy} onClick={() => { setEditingId(e.id); setEditText(e.text); }}>Edit</Button>
                  <Button appearance="ghost" size="small" disabled={busy} onClick={() => attempt(() => api.rejectPendingMemory(e.id))}>Reject</Button>
                </>
              )}
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}

function MemoryCard({ onChanged, pendingMemory }: CardProps) {
  const [memory, setMemory] = useState<HubMemory>({ scopes: [] });
  const [pending, setPending] = useState<PendingMemoryEntry[]>([]);
  const [scope, setScope] = useState("shared");
  const [text, setText] = useState("");
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setMemory((await api.getHubMemory()) ?? { scopes: [] });
      setPending((await api.getPendingMemory()) ?? []);
    } catch (e) {
      setError(errText(e));
    }
  }, []);
  // Approving or rejecting changes the count on the nav badge, so refresh the hub state too.
  const loadAll = useCallback(async () => { await load(); await onChanged?.(); }, [load, onChanged]);
  // The nav badge follows the polled count, so reload when it changes: a proposal that arrives while this card is open shows up.
  useEffect(() => { void load(); }, [load, pendingMemory]);

  const scopes = [...memory.scopes].sort((a, b) => Number(b.scope === "shared") - Number(a.scope === "shared"));
  // The Controller's memory is Shared memory, so it is not offered as a scope of its own.
  const pickable = scopes.filter((s) => s.scope !== "controller");
  const scopeOptions = (pickable.length ? pickable : [{ scope: "shared", label: "Shared", entries: [] }]).map((s) => ({ id: s.scope, label: s.label }));
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
      {pending.length > 0 && <PendingMemoryGroup entries={pending} onChanged={loadAll} setError={setError} />}
      {total === 0 ? (
        <Text subdued>No entries yet. Agents add entries as they work, or add one above.</Text>
      ) : (
        scopes.filter((s) => s.entries.length > 0).map((s) => <ScopeGroup key={s.scope} scope={s} onChanged={load} setError={setError} />)
      )}
    </section>
  );
}

const SECTIONS = [
  { id: "environment", label: "Environment", Card: EnvironmentCard },
  { id: "limits", label: "Spend limits", Card: SpendLimitsCard },
  { id: "personas", label: "Personas", Card: PersonasCard },
  { id: "memory", label: "Agent memory", Card: MemoryCard },
] as const;

export default function SettingsScreen({ state, onChanged, routeId }: ScreenProps) {
  const { id: section, Card } = SECTIONS.find((s) => s.id === routeId) ?? SECTIONS[0];
  return (
    <>
      <Text variant="headline" el="h2" size="large">Settings</Text>
      <Text subdued>Applies to all sessions in this fyrd</Text>
      <div className="settings">
        <nav aria-label="Settings sections" className="settings-nav">
          {SECTIONS.map((s) => (
            <button
              key={s.id}
              type="button"
              className="row-btn"
              aria-current={s.id === section ? "page" : undefined}
              onClick={() => navigate("settings", s.id)}
            >
              {s.label}
            </button>
          ))}
        </nav>
        <div className="settings-body"><Card budget={state?.budget} pendingMemory={state?.pendingMemory} onChanged={onChanged} /></div>
      </div>
    </>
  );
}
