import { useEffect, useState } from "react";
import * as api from "../api";
import type { MemoryDoc, MemoryKind } from "../types";

interface SectionProps {
  title: string;
  initial: string;
  version: string;
  kind: MemoryKind;
  repo?: string;
}

function pick(d: MemoryDoc, kind: MemoryKind, repo?: string): { text: string; version: string } | undefined {
  if (kind === "owner-proxy") return { text: d.ownerProxy, version: d.ownerProxyVersion };
  if (kind === "global") return { text: d.global, version: d.globalVersion };
  return d.repos.find((r) => r.repo === repo);
}

function Section({ title, initial, version: initialVersion, kind, repo }: SectionProps) {
  const [text, setText] = useState(initial);
  const [version, setVersion] = useState(initialVersion);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function save() {
    if (busy) return;
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      await api.saveMemory(kind, text, repo, version);
      setSaved(true);
      // Keep the owner's text; only pick up the new version so the next save is not a false conflict.
      try {
        const v = pick(await api.getMemory(), kind, repo)?.version;
        if (v !== undefined) setVersion(v);
      } catch {
        /* the next save will report a conflict if the version is stale */
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  async function reload() {
    if (busy) return;
    setBusy(true);
    try {
      const fresh = pick(await api.getMemory(), kind, repo);
      if (fresh) {
        setText(fresh.text);
        setVersion(fresh.version);
      }
      setError(null);
      setSaved(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="memory-section">
      <h3>{title}</h3>
      <textarea
        className="pane"
        aria-label={title}
        value={text}
        onChange={(e) => {
          setText(e.target.value);
          setSaved(false);
        }}
      />
      <div className="row">
        <button type="button" aria-label={`Save ${title}`} disabled={busy} onClick={() => void save()}>Save</button>
        <button type="button" aria-label={`Reload ${title}`} disabled={busy} onClick={() => void reload()}>Reload</button>
        {saved && <span className="saved">Saved</span>}
      </div>
      {error && <p className="error" role="alert">{error}</p>}
    </section>
  );
}

export default function MemoryTab() {
  // Loaded once: the 3 s poll must never overwrite what the owner is typing.
  const [doc, setDoc] = useState<MemoryDoc | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    api.getMemory()
      .then((d) => live && setDoc(d))
      .catch((e) => live && setError(e instanceof Error ? e.message : String(e)));
    return () => {
      live = false;
    };
  }, []);

  return (
    <div className="memory">
      <p className="note">Changes apply to specs launched from now on.</p>
      {error && <p className="error" role="alert">{error}</p>}
      {doc && (
        <>
          <Section title="Owner preferences" initial={doc.ownerProxy} version={doc.ownerProxyVersion} kind="owner-proxy" />
          <Section title="Global decisions" initial={doc.global} version={doc.globalVersion} kind="global" />
          {doc.repos.map((r) => (
            <Section key={r.repo} title={r.repo} initial={r.text} version={r.version} kind="repo" repo={r.repo} />
          ))}
        </>
      )}
    </div>
  );
}
