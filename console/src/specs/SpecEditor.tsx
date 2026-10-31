import { useEffect, useState } from "react";
import * as api from "../api";

interface Props {
  specId: string;
  onClose: () => void;
  onSaved: () => void;
}

export default function SpecEditor({ specId, onClose, onSaved }: Props) {
  const [text, setText] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    api.getSpec(specId)
      .then((s) => live && setText(s.markdown))
      .catch((e) => live && setError(e instanceof Error ? e.message : String(e)));
    return () => {
      live = false;
    };
  }, [specId]);

  async function save() {
    if (busy || text === null) return;
    setBusy(true);
    setError(null);
    try {
      await api.updateMarkdown(specId, text);
      onSaved();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="panel spec-editor" aria-label="Edit spec">
      <header className="panel-head">
        <h3>Edit spec</h3>
      </header>
      {text !== null && (
        <textarea
          className="pane"
          aria-label="Spec markdown"
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
      )}
      {error && <p className="error" role="alert">{error}</p>}
      <div className="row">
        <button type="button" disabled={busy || text === null} onClick={() => void save()}>Save</button>
        <button type="button" onClick={onClose}>Cancel</button>
      </div>
    </section>
  );
}
