import { useEffect, useState } from "react";
import * as api from "../api";
import type { MemoryDoc, MemoryKind } from "../types";

interface SectionProps {
  title: string;
  initial: string;
  kind: MemoryKind;
  repo?: string;
}

function Section({ title, initial, kind, repo }: SectionProps) {
  const [text, setText] = useState(initial);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function save() {
    if (busy) return;
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      await api.saveMemory(kind, text, repo);
      setSaved(true);
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
          <Section title="Owner preferences" initial={doc.ownerProxy} kind="owner-proxy" />
          <Section title="Global decisions" initial={doc.global} kind="global" />
          {doc.repos.map((r) => (
            <Section key={r.repo} title={r.repo} initial={r.text} kind="repo" repo={r.repo} />
          ))}
        </>
      )}
    </div>
  );
}
