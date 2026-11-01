import { useState } from "react";
import { Alert, Button, Checkbox, TextField } from "@servicetitan/anvil2";
import * as api from "../api";

/** "owner/repo", or any github.com URL under that repo (clone, tree or file links). */
export function parseRepo(input: string): { owner: string; repo: string } | null {
  const s = input.trim().replace(/^git@github\.com:/, "https://github.com/");
  const m = /^(?:https?:\/\/(?:www\.)?github\.com\/)?([A-Za-z0-9-]+)\/([A-Za-z0-9._-]+?)(?:\.git)?(?:[/?#].*)?$/.exec(s);
  return m ? { owner: m[1], repo: m[2] } : null;
}

/** The message a session gets to attach a repo to itself; personas already know these connector tools. */
export const addRepoMessage = (owner: string, repo: string, push: boolean) =>
  `Attach the GitHub repository ${owner}/${repo} to this session: call \`mcp__claude-code-remote__add_repo\` on yourself ` +
  `with owner \`${owner}\`, repo \`${repo}\` and access \`${push ? "push" : "read"}\`, clone it, then call ` +
  "`mcp__claude-code-remote__register_repo_root` with the clone's path. Reply with that path when done.";

export default function AddRepo({ sessionId }: { sessionId: string }) {
  const [value, setValue] = useState("");
  const [push, setPush] = useState(true);
  const [status, setStatus] = useState<{ ok: boolean; text: string } | null>(null);
  const parsed = parseRepo(value);

  const send = async () => {
    if (!parsed) return;
    setStatus(null);
    try {
      await api.sendMessage(sessionId, addRepoMessage(parsed.owner, parsed.repo, push));
      setStatus({ ok: true, text: `Asked the session to attach ${parsed.owner}/${parsed.repo}.` });
      setValue("");
    } catch (e) {
      setStatus({ ok: false, text: e instanceof Error ? e.message : String(e) });
    }
  };

  return (
    <div className="add-repo">
      <TextField
        label="Add GitHub repository"
        placeholder="owner/repo or GitHub URL"
        value={value}
        onChange={(e) => setValue(e.target.value)}
        error={value.trim() !== "" && !parsed ? "Use owner/repo or a github.com link." : undefined}
      />
      <Checkbox label="Allow push" checked={push} onChange={(e) => setPush(e.target.checked)} />
      <div>
        <Button appearance="secondary" size="small" disabled={!parsed} onClick={send}>Add repository</Button>
      </div>
      {status && <Alert status={status.ok ? "success" : "danger"} title={status.text} role={status.ok ? "status" : "alert"} />}
    </div>
  );
}
