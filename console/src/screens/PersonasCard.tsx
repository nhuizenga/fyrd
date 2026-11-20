import { useCallback, useEffect, useMemo, useState } from "react";
import { Alert, Button, Text } from "@servicetitan/anvil2";
import { DataTable, createColumnHelper, type TableRow } from "@servicetitan/anvil2/beta";
import * as api from "../api";
import type { HubPersona } from "../types";

const MODELS = [
  { id: "claude-opus-5-5", label: "Opus 5.5" },
  { id: "claude-sonnet-5-5", label: "Sonnet 5.5" },
  { id: "claude-haiku-5-5", label: "Haiku 5.5" },
  { id: "claude-fable-5-2", label: "Fable 5.2" },
  { id: "claude-fable-5-1", label: "Fable 5.1" },
];
const MODEL_OPTIONS = MODELS.map((m) => ({ id: m.id, label: `${m.label} · ${m.id}` }));
const APPLY_NOTE = "Changes apply to sessions created from now on.";
const errText = (e: unknown) => (e instanceof Error ? e.message : String(e));
const modelLabel = (id: string | null | undefined) => MODELS.find((m) => m.id === id)?.label ?? id ?? "";

type PersonaRow = { id: string; name: string; model: string; defaultModel: string };

/**
 * One row per persona with its model, which is all the owner changes here: each persona's name and text come from its
 * skill in the fyrd repo (`skills/<id>/SKILL.md`).
 */
export default function PersonasCard() {
  const [personas, setPersonas] = useState<HubPersona[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setPersonas((await api.getPersonas()) ?? []);
    } catch (e) {
      setError(errText(e));
    }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const change = useCallback(async (action: () => Promise<HubPersona>) => {
    setError(null);
    setSaved(null);
    try {
      const p = await action();
      setPersonas((list) => list.map((x) => (x.id === p.id ? p : x)));
      setSaved(`${p.name} now uses ${modelLabel(p.model)}.`);
    } catch (e) {
      setError(errText(e));
      await load();
    }
  }, [load]);

  const rows: TableRow<PersonaRow>[] = personas.map((p) => ({
    id: p.id, name: p.name, model: p.model, defaultModel: p.defaultModel ?? p.model,
  }));

  const columns = useMemo(() => {
    const column = createColumnHelper<PersonaRow>();
    return [
      column("name", { header: { label: "Persona" } }),
      column("model", {
        header: { label: "Model" },
        renderCell: (value: string) => modelLabel(value),
        editConfig: {
          mode: "select",
          options: MODEL_OPTIONS,
          disableSearch: true,
          onChange: (option, rowId) => {
            if (option) void change(() => api.setPersonaModel(rowId, String(option.id)));
          },
        },
      }),
      column("defaultModel", {
        header: { label: "Default" },
        renderCell: (value: string, { row }: { row: PersonaRow }) => (
          <div className="persona-default">
            <span>{modelLabel(value)}</span>
            {row.model !== value && (
              <Button appearance="ghost" size="small" aria-label={`Reset ${row.name} to the default model`}
                onClick={() => void change(() => api.resetPersona(row.id))}>
                Reset
              </Button>
            )}
          </div>
        ),
      }),
    ];
  }, [change]);

  return (
    <section className="card" aria-labelledby="personas-h">
      <Text variant="headline" el="h3" size="medium" id="personas-h">Personas</Text>
      <Text subdued>
        Pick the Claude model each persona runs on. What a persona does comes from its skill in the fyrd repo
        (skills/&lt;id&gt;/SKILL.md), so updating the repo updates it.
      </Text>
      {error && <Alert status="danger" title={error} role="alert" />}
      {saved && <Text role="status">{saved}</Text>}
      <DataTable aria-label="Personas" columns={columns} data={rows} />
      <Text size="small" subdued>{APPLY_NOTE}</Text>
    </section>
  );
}
