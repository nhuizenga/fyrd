import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Checkbox, Chip, Text, TextField, Textarea } from "@servicetitan/anvil2";
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

/** Enough of a hash to compare by eye with the first line of a pasted block. */
export const shortHash = (hash: string) => hash.slice(0, 12);

/** When a change reaches sessions: the Controller creates them, and it is sent new instructions only while it is idle. */
const WHEN = "the Controller creates after it has taken its new instructions, which it gets the next time it is idle";

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

const GUIDE_ID = "session-skills-guide";

function SetupScript({ setup, onChange, setError }: { setup: SessionSkillSetup; onChange: (s: SessionSkillSetup) => void; setError: (e: string | null) => void }) {
  const [copied, setCopied] = useState(false);
  // The hash of the script this tab last put on the clipboard: that script, not the one on screen now, is what the owner pasted.
  const [copiedHash, setCopiedHash] = useState<string | null>(null);
  // The hash the owner vouched for without copying here (pasted in an earlier visit, or copied by hand).
  const [vouchedHash, setVouchedHash] = useState<string | null>(null);
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

  const copy = async () => {
    setError(null);
    try {
      // No clipboard on a page that is not a secure context, or in an older browser.
      if (!navigator.clipboard) throw new Error("no clipboard");
      await navigator.clipboard.writeText(setup.script);
      setCopiedHash(setup.hash);
      setCopied(true);
    } catch {
      setError("Could not copy. Select the script and copy it by hand.");
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

  const installed = setup.mode === "installed";
  const changedSinceCopy = copiedHash !== null && copiedHash !== setup.hash;
  const vouched = vouchedHash === setup.hash;
  // What is confirmed is the script that was pasted: the one copied here, else the one the owner vouches for.
  const pastedHash = copiedHash ?? (vouched ? setup.hash : null);

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
      <Text size="small" subdued>
        This script's first line holds the hash that starts with <code>{shortHash(setup.hash)}</code>.
      </Text>
      {!installed && changedSinceCopy && (
        <Alert status="warning" role="status" title="The script changed since you copied it">
          The block you copied starts with {shortHash(copiedHash)} and is no longer the current one. Copy the script again, paste it over the old block, then confirm.
        </Alert>
      )}
      {!installed && copiedHash === null && (
        <Checkbox
          label={`I pasted this script without copying it here just now, and the first line of the block in the environment holds the hash that starts with ${shortHash(setup.hash)}`}
          checked={vouched}
          onChange={(e) => setVouchedHash(e.target.checked ? setup.hash : null)}
        />
      )}
      <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
        <Button appearance="secondary" onClick={copy}>Copy script</Button>
        <Button
          appearance="primary"
          disabled={busy || installed || changedSinceCopy || pastedHash === null}
          onClick={() => pastedHash !== null && run(() => api.confirmSessionSkillSetup(pastedHash))}
        >
          I've pasted this script
        </Button>
        {setup.confirmedHash && (
          <Button appearance="ghost" disabled={busy} onClick={() => run(api.clearSessionSkillSetup)}>Stop using the script</Button>
        )}
        <Button appearance="ghost" aria-expanded={guide !== null} aria-controls={GUIDE_ID} onClick={toggleGuide}>{guide !== null ? "Hide the setup guide" : "Setup guide"}</Button>
        <Text role="status" size="small">{copied ? "Copied." : ""}</Text>
      </div>
      {!installed && copiedHash === null && !vouched && (
        <Text size="small" subdued>To confirm, copy the script here and paste it, or tick the box above if the block is already in the environment.</Text>
      )}
      {setup.confirmedHash && (
        <Text size="small" subdued>
          "Stop using the script" puts the whole text back into the instructions of the sessions {WHEN}. Remove the block from the environment yourself, after that.
        </Text>
      )}
      <section id={GUIDE_ID} aria-label="Setup guide" className="card" hidden={guide === null}>{guide !== null && <Markdown text={guide} />}</section>
    </>
  );
}

/** Settings, Session skills: the skills (list, edit, reset, delete), the setup script made from them, and its state. */
export default function SessionSkillsCard() {
  const [skills, setSkills] = useState<SessionSkill[]>([]);
  const [setup, setSetup] = useState<SessionSkillSetup | null>(null);
  const [d, setDraft] = useState<Draft | null>(null);
  // The draft as it was opened: a draft that differs from it has changes that are not saved.
  const [opened, setOpened] = useState<Draft | null>(null);
  // What the owner asked for that needs a yes first: deleting the open skill, or leaving a changed draft for another.
  const [asking, setAsking] = useState<{ kind: "delete" } | { kind: "leave"; next: Draft | null } | null>(null);
  const [busy, setBusy] = useState(false);
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

  const open = (next: Draft | null) => {
    setDraft(next);
    setOpened(next);
    setAsking(null);
  };
  const dirty = !!d && !!opened && (d.name !== opened.name || d.description !== opened.description || d.roles !== opened.roles || d.body !== opened.body);
  /** Opens another draft (or none), asking first when the open one has changes that would be lost. */
  const leaveFor = (next: Draft | null) => (dirty ? setAsking({ kind: "leave", next }) : open(next));

  const patch = (p: Partial<Draft>) => setDraft((x) => (x ? { ...x, ...p } : x));
  const words = d ? wordCount(d.body) : 0;

  const attempt = async (action: () => Promise<void>) => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await action();
      await load();
    } catch (e) {
      setError(errText(e));
    } finally {
      setBusy(false);
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
      open(null);
    });
  };

  const missing = setup?.missingDefaults ?? [];

  return (
    <section className="card" aria-labelledby="session-skills-h">
      <Text variant="headline" el="h3" size="medium" id="session-skills-h">Session skills</Text>
      <Text subdued>
        Parts of a role's instructions that only some tasks need. A session loads one with the Skill tool when its situation comes up, so every session's instructions stay shorter.
      </Text>
      {error && <Alert status="danger" title={error} role="alert" />}
      {setup && <SetupStatus setup={setup} />}
      <div>
        <Button appearance="secondary" onClick={() => leaveFor({ name: "fyrd-", description: "", roles: "builder", body: "", isNew: true, seeded: false, updateAvailable: false })}>
          Add session skill
        </Button>
      </div>
      <div className="split">
        <div>
          <ul style={{ listStyle: "none", margin: 0, padding: 0 }}>
            {skills.map((s) => (
              <li key={s.name}>
                <button type="button" className="row-btn" aria-current={!!d && !d.isNew && d.name === s.name} onClick={() => leaveFor(draftOf(s))}>
                  {s.name}{" "}
                  {s.updateAvailable && <Chip label="Update available" size="small" />}
                </button>
              </li>
            ))}
          </ul>
          {missing.length > 0 && (
            <section aria-label="Deleted defaults" style={{ display: "flex", flexDirection: "column", gap: 8, marginTop: 12 }}>
              <Text size="small" subdued>Defaults you deleted. The roles they belong to are without these rules until you restore them.</Text>
              {missing.map((name) => (
                <div key={name} style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
                  <Text size="small">{name}</Text>
                  <Button
                    appearance="secondary"
                    size="small"
                    disabled={busy}
                    aria-label={`Restore default ${name}`}
                    onClick={() => attempt(async () => { await api.resetSessionSkill(name); })}
                  >
                    Restore default
                  </Button>
                </div>
              ))}
            </section>
          )}
        </div>
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          {asking?.kind === "leave" && d && (
            <Alert status="warning" role="alertdialog" aria-label="Unsaved changes" title={`Your changes to ${d.isNew ? "the new skill" : d.name} are not saved`}>
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap", marginTop: 8 }}>
                <Button appearance="secondary" onClick={() => open(asking.next)}>Discard changes</Button>
                <Button appearance="ghost" onClick={() => setAsking(null)}>Keep editing</Button>
              </div>
            </Alert>
          )}
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
                  <Button appearance="secondary" disabled={busy} onClick={() => attempt(async () => open(draftOf(await api.resetSessionSkill(d.name))))}>Reset to default</Button>
                </div>
              )}
              <Text size="small" subdued>
                {setup?.confirmedHash
                  ? `Saving a change makes the pasted setup script out of date: sessions ${WHEN}, get the inline text until you paste the new script and confirm.`
                  : `Changes apply to sessions ${WHEN}.`}
              </Text>
              {asking?.kind === "delete" && !d.isNew && (
                <Alert status="warning" role="alertdialog" aria-label="Delete session skill" title={`Delete ${d.name}?`}>
                  Sessions of the roles it belongs to ({opened?.roles || d.roles}) lose these rules: nothing else in their instructions holds them.
                  {d.seeded ? " You can restore the default afterwards." : " It can't be brought back."}
                  <div style={{ display: "flex", gap: 8, flexWrap: "wrap", marginTop: 8 }}>
                    <Button appearance="primary" disabled={busy} onClick={() => attempt(async () => { await api.deleteSessionSkill(d.name); open(null); })}>Delete it</Button>
                    <Button appearance="ghost" onClick={() => setAsking(null)}>Keep it</Button>
                  </div>
                </Alert>
              )}
              <div style={{ display: "flex", gap: 8 }}>
                <Button appearance="primary" disabled={busy} onClick={save}>Save session skill</Button>
                <Button appearance="ghost" onClick={() => open(null)}>Discard</Button>
                {!d.isNew && (
                  <Button appearance="secondary" disabled={busy || asking?.kind === "delete"} onClick={() => setAsking({ kind: "delete" })}>Delete session skill</Button>
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
