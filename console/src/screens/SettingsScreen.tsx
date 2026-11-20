import { lazy, Suspense, useCallback, useEffect, useState } from "react";
import { Alert, Button, Chip, SelectFieldSync, Text, TextField, Textarea } from "@servicetitan/anvil2";
import DeleteIcon from "@servicetitan/anvil2/assets/icons/material/round/delete.svg?react";
import EditIcon from "@servicetitan/anvil2/assets/icons/material/round/edit.svg?react";
import * as api from "../api";
import { navigate } from "../route";
import { relativeTime } from "../time";
import { getItemLimit, MAX_ITEM_LIMIT, MIN_ITEM_LIMIT, setItemLimit } from "../useTranscript";
import type { HubBudgetView, SecretName, SecretsView, HubMemory, MemoryEntry, MemoryScopeView, PendingMemoryEntry } from "../types";
import type { ScreenProps } from "./ScreenProps";
import SessionSkillsCard from "./SessionSkillsCard";

/** Shown when the persona table's chunk fails to load (a network blip, or asset names changed by a rebuild). */
export function PersonasLoadFailed() {
  return (
    <Alert status="danger" title="Couldn't load the persona table" role="alert">
      <Button onClick={() => window.location.reload()}>Reload</Button>
    </Alert>
  );
}

// The persona table is Anvil's DataTable, which is large: it loads when the Personas section first opens. A chunk that
// fails to load shows an alert in its place instead of blanking the console.
const PersonasTable = lazy(() => import("./PersonasCard").catch(() => ({ default: PersonasLoadFailed })));
function PersonasCard() {
  return <Suspense fallback={<Text subdued>Loading personas…</Text>}><PersonasTable /></Suspense>;
}

const APPLY_NOTE = "Changes apply to sessions created from now on.";
const errText = (e: unknown) => (e instanceof Error ? e.message : String(e));

/** A whole number from `min` to `max` typed in a field, or null. */
const parseWholeNumber = (v: string, min: number, max: number) => {
  const t = v.trim();
  const n = Number(t);
  return /^\d+$/.test(t) && n >= min && n <= max ? n : null;
};
const rangeMessage = (min: number, max: number) => `Enter a whole number from ${min} to ${max}.`;

const SECRET_FIELDS: { name: SecretName; label: string; envHint: string }[] = [
  { name: "anthropic-api-key", label: "Anthropic API Key", envHint: "ANTHROPIC_API_KEY" },
  { name: "github-token", label: "GitHub Token", envHint: "GH_TOKEN" },
];

/** One optional secret: a write-only password field, a status line and a way to remove the saved value. */
function SecretField({ field, view, supported, onChange }: {
  field: (typeof SECRET_FIELDS)[number];
  view: SecretsView["secrets"][number] | undefined;
  supported: boolean;
  onChange: (v: SecretsView) => void;
}) {
  const [value, setValue] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);

  const run = async (action: () => Promise<SecretsView>, done: string) => {
    setError(null);
    setSaved(null);
    try {
      const v = await action();
      setValue("");
      setSaved(done);
      if (v) onChange(v);
    } catch (e) {
      setError(errText(e));
    }
  };

  const status = !view || !view.set
    ? "Not set."
    : view.source === "saved" ? "A saved value is in use (it overrides the environment variable)." : `Using the ${field.envHint} environment variable.`;

  return (
    <div className="secret-field">
      {error && <Alert status="danger" title={error} role="alert" />}
      <TextField
        label={`${field.label} (optional)`}
        description={status}
        type="password"
        autoComplete="off"
        placeholder={view?.source === "saved" ? "Saved. Type to replace." : ""}
        value={value}
        disabled={!supported}
        onChange={(e) => setValue(e.target.value)}
      />
      {saved && <Text role="status" size="small">{saved}</Text>}
      <div className="row">
        <Button appearance="primary" aria-label={`Save ${field.label}`} disabled={!supported || !value.trim()}
          onClick={() => run(() => api.saveSecret(field.name, value.trim()), `${field.label} saved.`)}>
          Save
        </Button>
        {view?.source === "saved" && (
          <Button aria-label={`Remove saved ${field.label}`}
            onClick={() => run(() => api.removeSecret(field.name), `Saved ${field.label} removed.`)}>
            Remove saved value
          </Button>
        )}
      </div>
    </div>
  );
}

function SecretsSection() {
  const [secrets, setSecrets] = useState<SecretsView | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    (async () => {
      try {
        const v = await api.getSecrets();
        if (v) setSecrets(v);
      } catch (e) {
        setError(errText(e));
      }
    })();
  }, []);

  return (
    <>
      <Text variant="headline" el="h4" size="small">Optional keys</Text>
      <Text subdued>Saved encrypted on this computer (Windows DPAPI) and never shown again. A saved value overrides the environment variable; remove it to go back to the environment variable.</Text>
      {error && <Alert status="danger" title={error} role="alert" />}
      {secrets && !secrets.supported && <Alert status="warning" title="Saving keys needs Windows. Set the environment variable instead." />}
      {SECRET_FIELDS.map((f) => (
        <SecretField key={f.name} field={f} supported={secrets?.supported ?? true}
          view={secrets?.secrets.find((s) => s.name === f.name)} onChange={setSecrets} />
      ))}
    </>
  );
}

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
      <SecretsSection />
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

function DisplayCard(_: CardProps) {
  const [limit, setLimit] = useState(() => String(getItemLimit()));
  const [limitError, setLimitError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  return (
    <section className="card" aria-labelledby="display-h">
      <Text variant="headline" el="h3" size="medium" id="display-h">Display</Text>
      <Text subdued>Stored in this browser only.</Text>
      {saved && <Text role="status">{saved}</Text>}
      <TextField
        label="Messages shown per conversation"
        description={`The newest messages rendered when a session opens; "Load earlier messages" adds this many more. Whole number, ${MIN_ITEM_LIMIT} to ${MAX_ITEM_LIMIT}.`}
        inputMode="numeric"
        value={limit}
        error={limitError ?? undefined}
        onChange={(e) => setLimit(e.target.value)}
      />
      <div>
        <Button
          appearance="primary"
          aria-label="Save message limit"
          onClick={() => {
            const n = parseWholeNumber(limit, MIN_ITEM_LIMIT, MAX_ITEM_LIMIT);
            if (n === null) { setSaved(null); return setLimitError(rangeMessage(MIN_ITEM_LIMIT, MAX_ITEM_LIMIT)); }
            setLimitError(null);
            setLimit(String(n));
            setItemLimit(n);
            setSaved("Message limit saved. It applies the next time you open a session.");
          }}
        >
          Save
        </Button>
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
  { id: "display", label: "Display", Card: DisplayCard },
  { id: "personas", label: "Personas", Card: PersonasCard },
  { id: "session-skills", label: "Session skills", Card: SessionSkillsCard },
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
