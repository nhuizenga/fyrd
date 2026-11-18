import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Chip, Text, TextField, Textarea } from "@servicetitan/anvil2";
import * as api from "../api";
import Markdown from "../components/Markdown";
import type { SessionSkill, SessionSkillSetup } from "../types";

/** The server's limits (SessionSkills.cs), checked here too so a mistake shows before the request. */
export const MAX_SKILL_WORDS = 1500;
const NAME = /^fyrd-[a-z0-9]+(-[a-z0-9]+)*$/;
const MAX_NAME = 64;

const errText = (e: unknown) => (e instanceof Error ? e.message : String(e));
export const wordCount = (s: string) => s.split(/\s+/).filter(Boolean).length;
const parseRoles = (s: string) => [...new Set(s.split(",").map((r) => r.trim()).filter(Boolean))];

type Draft = { name: string; description: string; roles: string; body: string; isNew: boolean; seeded: boolean; updateAvailable: boolean };
const draftOf = (s: SessionSkill): Draft => ({
  name: s.name, description: s.description, roles: s.roles.join(", "), body: s.body,
  isNew: false, seeded: !!s.seedHash, updateAvailable: !!s.updateAvailable,
});

/** What sessions get right now, and what the owner has to do about it. */
function SetupStatus({ setup }: { setup: SessionSkillSetup }) {
  if (setup.mode === "out-of-date") {
    return (
      <Alert status="warning" role="status" title="The setup script is out of date">
        The skills changed after you pasted the script. Sessions use the inline text until you paste the new script and confirm.
      </Alert>
    );
  }
  return (
    <div role="status" aria-label="Session skills status" style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
      <Chip label={setup.mode === "installed" ? "Installed" : "Inline"} size="small" />
      <Text size="small">
        {setup.mode === "installed"
          ? "New sessions load these skills on demand from the environment's setup script."
          : "Sessions get the whole text of every skill in their instructions. To load it on demand instead, paste the setup script and confirm."}
      </Text>
    </div>
  );
}

function SetupScript({ setup, onChange, setError }: { setup: SessionSkillSetup; onChange: (s: SessionSkillSetup) => void; setError: (e: string | null) => void }) {
  const [copied, setCopied] = useState(false);
  const [guide, setGuide] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!copied) return;
    const t = setTimeout(() => setCopied(false), 1500);
    return () => clearTimeout(t);
  }, [copied]);

  const run = async (action: () => Promise<SessionSkillSetup>) => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      onChange(await action());
    } catch (e) {
      setError(errText(e));
      // A 409 means the skills changed under this script: show the current one.
      try { onChange(await api.getSessionSkillSetup()); } catch { /* the error above stands */ }
    } finally {
      setBusy(false);
    }
  };

  const toggleGuide = async () => {
    if (guide !== null) return setGuide(null);
    setError(null);
    try {
      setGuide((await api.getSessionSkillGuide()).text);
    } catch (e) {
      setError(errText(e));
    }
  };

  return (
    <>
      <Text variant="headline" el="h4" size="small">Setup script</Text>
      <Text size="small" subdued>
        {setup.environmentId
          ? `Paste this block into the setup script of environment ${setup.environmentId} at claude.ai/code, replacing an older Fyrd block between the markers and keeping anything else in the script. `
          : "Fyrd has no environment set (Settings, Environment), so sessions start in the Controller's environment: paste this block into that environment's setup script at claude.ai/code, replacing an older Fyrd block between the markers and keeping anything else in the script. "}
        It writes the skills to {setup.skillsDir} for sessions created afterwards.
      </Text>
      <Textarea label="Setup script" rows={8} readOnly value={setup.script} style={{ fontFamily: "ui-monospace, SFMono-Regular, Consolas, monospace" }} />
      <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
        <Button appearance="secondary" onClick={() => navigator.clipboard.writeText(setup.script).then(() => setCopied(true), () => setError("Could not copy. Select the script and copy it by hand."))}>
          Copy script
        </Button>
        <Button appearance="primary" disabled={busy || setup.mode === "installed"} onClick={() => run(() => api.confirmSessionSkillSetup(setup.hash))}>
          I've pasted this script
        </Button>
        {setup.confirmedHash && (
          <Button appearance="ghost" disabled={busy} onClick={() => run(api.clearSessionSkillSetup)}>Stop using the script</Button>
        )}
        <Button appearance="ghost" aria-expanded={guide !== null} onClick={toggleGuide}>{guide !== null ? "Hide the setup guide" : "Setup guide"}</Button>
        {copied && <Text role="status" size="small">Copied.</Text>}
      </div>
      {setup.confirmedHash && (
        <Text size="small" subdued>
          "Stop using the script" puts the whole text back into every session's instructions. Remove the block from the environment yourself.
        </Text>
      )}
      {guide !== null && <section aria-label="Setup guide" className="card"><Markdown text={guide} /></section>}
    </>
  );
}

/** Settings, Session skills: the skills (list, edit, reset, delete), the setup script made from them, and its state. */
export default function SessionSkillsCard() {
  const [skills, setSkills] = useState<SessionSkill[]>([]);
  const [setup, setSetup] = useState<SessionSkillSetup | null>(null);
  const [d, setDraft] = useState<Draft | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setSkills((await api.getSessionSkills()) ?? []);
      setSetup((await api.getSessionSkillSetup()) ?? null);
    } catch (e) {
      setError(errText(e));
    }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const patch = (p: Partial<Draft>) => setDraft((x) => (x ? { ...x, ...p } : x));
  const words = d ? wordCount(d.body) : 0;

  const attempt = async (action: () => Promise<void>) => {
    setError(null);
    try {
      await action();
      await load();
    } catch (e) {
      setError(errText(e));
    }
  };

  const save = () => {
    if (!d) return;
    const name = d.name.trim();
    const roles = parseRoles(d.roles);
    const problem =
      !NAME.test(name) || name.length > MAX_NAME ? "Name the skill fyrd- followed by lower-case words joined by hyphens, such as fyrd-release-notes."
      : d.isNew && skills.some((s) => s.name === name) ? "A session skill with this name already exists."
      : !d.description.trim() ? "Give the skill a description: what it holds and when to load it."
      : roles.length === 0 ? "Name at least one role, such as builder."
      : !d.body.trim() ? "Give the skill a body."
      : words > MAX_SKILL_WORDS ? `The body has ${words} words; a session skill holds at most ${MAX_SKILL_WORDS}.`
      : null;
    if (problem) return setError(problem);
    void attempt(async () => {
      await api.saveSessionSkill({ name, description: d.description.trim(), roles, body: d.body });
      setDraft(null);
    });
  };

  return (
    <section className="card" aria-labelledby="session-skills-h">
      <Text variant="headline" el="h3" size="medium" id="session-skills-h">Session skills</Text>
      <Text subdued>
        Parts of a role's instructions that only some tasks need. A session loads one with the Skill tool when its situation comes up, so every session's instructions stay shorter.
      </Text>
      {error && <Alert status="danger" title={error} role="alert" />}
      {setup && <SetupStatus setup={setup} />}
      <div>
        <Button appearance="secondary" onClick={() => setDraft({ name: "fyrd-", description: "", roles: "builder", body: "", isNew: true, seeded: false, updateAvailable: false })}>
          Add session skill
        </Button>
      </div>
      <div className="split">
        <ul style={{ listStyle: "none", margin: 0, padding: 0 }}>
          {skills.map((s) => (
            <li key={s.name}>
              <button type="button" className="row-btn" aria-current={!!d && !d.isNew && d.name === s.name} onClick={() => setDraft(draftOf(s))}>
                {s.name}
                {s.updateAvailable && <Chip label="Update available" size="small" />}
              </button>
            </li>
          ))}
        </ul>
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          {!d ? (
            <Text subdued>Select a session skill to edit it.</Text>
          ) : (
            <>
              <TextField
                label="Name"
                value={d.name}
                disabled={!d.isNew}
                hint={d.isNew ? "fyrd- followed by lower-case words joined by hyphens." : "A skill keeps its name: sessions load it by that name."}
                onChange={(e) => patch({ name: e.target.value })}
              />
              <TextField
                label="Description"
                description="One line: what the skill holds and when to load it. A session sees this line, and decides from it."
                value={d.description}
                onChange={(e) => patch({ description: e.target.value })}
              />
              <TextField
                label="Roles"
                description="The persona ids whose sessions get this skill, separated by commas."
                value={d.roles}
                onChange={(e) => patch({ roles: e.target.value })}
              />
              <Textarea
                label="Body"
                description={`${words} of ${MAX_SKILL_WORDS} words. Put the rules that matter most first.`}
                rows={12}
                value={d.body}
                onChange={(e) => patch({ body: e.target.value })}
              />
              {!d.isNew && d.seeded && (
                <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
                  {d.updateAvailable && <Text size="small">A newer default is available for this skill.</Text>}
                  <Button appearance="secondary" onClick={() => attempt(async () => setDraft(draftOf(await api.resetSessionSkill(d.name))))}>Reset to default</Button>
                </div>
              )}
              <Text size="small" subdued>
                {setup?.confirmedHash
                  ? "Saving a change makes the pasted setup script out of date: sessions created from now on get the inline text until you paste the new script and confirm."
                  : "Changes apply to sessions created from now on."}
              </Text>
              <div style={{ display: "flex", gap: 8 }}>
                <Button appearance="primary" onClick={save}>Save session skill</Button>
                <Button appearance="ghost" onClick={() => setDraft(null)}>Discard</Button>
                {!d.isNew && (
                  <Button appearance="secondary" onClick={() => attempt(async () => { await api.deleteSessionSkill(d.name); setDraft(null); })}>Delete session skill</Button>
                )}
              </div>
            </>
          )}
        </div>
      </div>
      {setup && <SetupScript setup={setup} onChange={setSetup} setError={setError} />}
    </section>
  );
}
