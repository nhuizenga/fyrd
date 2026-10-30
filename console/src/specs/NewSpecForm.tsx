import { useState } from "react";
import * as api from "../api";

interface Props {
  onCreated: () => void;
  onCancel: () => void;
}

export const DEFAULT_REPO = "servicetitan/titan-lens-ossie";

export default function NewSpecForm({ onCreated, onCancel }: Props) {
  const [title, setTitle] = useState("");
  const [repo, setRepo] = useState(DEFAULT_REPO);
  const [markdown, setMarkdown] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await api.createSpec({ title, repo, markdown });
      onCreated();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  async function upload(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (file) setMarkdown(await readText(file));
  }

  return (
    <form className="new-spec" onSubmit={submit}>
      <label>
        Title
        <input value={title} onChange={(e) => setTitle(e.target.value)} />
      </label>
      <label>
        Repo
        <input value={repo} onChange={(e) => setRepo(e.target.value)} />
      </label>
      <label>
        Upload .md
        <input type="file" accept=".md,text/markdown" onChange={upload} />
      </label>
      <label>
        Markdown
        <textarea rows={10} value={markdown} onChange={(e) => setMarkdown(e.target.value)} />
      </label>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="row">
        <button type="submit" disabled={busy}>Create</button>
        <button type="button" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}

function readText(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error);
    reader.readAsText(file);
  });
}
