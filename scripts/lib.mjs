import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";

export function runDir(home = os.homedir()) {
  return path.join(home, ".fyrd", "run");
}

const serverFile = (dir) => path.join(dir, "server.json");

export function readServerFile(dir) {
  try {
    const info = JSON.parse(fs.readFileSync(serverFile(dir), "utf8"));
    if (!info || !Number.isInteger(info.pid) || !Number.isInteger(info.port)) return null;
    return info;
  } catch {
    return null;
  }
}

export function writeServerFile(dir, info) {
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(serverFile(dir), JSON.stringify(info));
}

export function removeServerFile(dir) {
  fs.rmSync(serverFile(dir), { force: true });
}

export async function isServerAlive(info, fetchImpl = fetch) {
  try {
    process.kill(info.pid, 0);
  } catch {
    return false;
  }
  try {
    const res = await fetchImpl(`http://127.0.0.1:${info.port}/api/health`, {
      signal: AbortSignal.timeout(1000),
    });
    const body = await res.json();
    return body?.status === "ok";
  } catch {
    return false;
  }
}

function canListen(port) {
  return new Promise((resolve) => {
    const s = net.createServer();
    s.once("error", () => resolve(false));
    s.listen(port, "127.0.0.1", () => s.close(() => resolve(true)));
  });
}

export async function findFreePort(start = 4870, tries = 10) {
  for (let p = start; p < start + tries; p++) {
    if (await canListen(p)) return p;
  }
  throw new Error(`No free port in ${start}-${start + tries - 1}`);
}

function newestMtime(dir) {
  let newest = 0;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    const t = e.isDirectory() ? newestMtime(p) : fs.statSync(p).mtimeMs;
    if (t > newest) newest = t;
  }
  return newest;
}

export function needsConsoleBuild(repoRoot) {
  const index = path.join(repoRoot, "runtime/src/Fyrd.Server/wwwroot/index.html");
  if (!fs.existsSync(index)) return true;
  const src = path.join(repoRoot, "console/src");
  if (!fs.existsSync(src)) return false;
  return newestMtime(src) > fs.statSync(index).mtimeMs;
}

/** The one-line warning for `up` when no GitHub token is in the environment, else null. */
export function tokenWarning(env = process.env) {
  if (env.GH_TOKEN || env.GITHUB_TOKEN) return null;
  return "GH_TOKEN is not set: PR status and PR actions are off. Set it and run /fyrd:down then /fyrd:up.";
}

/** The hub state from /api/hub/state, or null when it can't be read (unreachable, no answer in 3 s, non-2xx, or not JSON). */
export async function readHubState(url, fetchImpl = fetch) {
  try {
    const res = await fetchImpl(`${url}/api/hub/state`, { signal: AbortSignal.timeout(3000) });
    return res.ok ? await res.json() : null;
  } catch {
    return null;
  }
}

/** The first `status` line: the URL, then the session and waiting-question counts of /api/hub/state when it could be read. */
export function statusLine(url, state) {
  if (!state) return `Fyrd running at ${url}`;
  const count = (n, noun) => `${n} ${noun}${n === 1 ? "" : "s"}`;
  return `Fyrd running at ${url} · ${count(state.sessions?.length ?? 0, "session")} · ${count(state.questions?.length ?? 0, "question")} waiting`;
}

/** The `status` line for the `gitHub` part of /api/hub/state. */
export function gitHubLine(gitHub) {
  if (gitHub?.configured && gitHub.login) return `GitHub: signed in as ${gitHub.login}`;
  if (gitHub?.configured && gitHub.error) return `GitHub: ${gitHub.error}`;
  return "GitHub: not configured";
}

/** The session-skill state from /api/hub/session-skills/setup-script, or null when it can't be read (as readHubState). */
export async function readSessionSkills(url, fetchImpl = fetch) {
  try {
    const res = await fetchImpl(`${url}/api/hub/session-skills/setup-script`, { signal: AbortSignal.timeout(3000) });
    return res.ok ? await res.json() : null;
  } catch {
    return null;
  }
}

/** The `status` line for session skills: which mode sessions get them in, and what to do when the pasted script is stale. */
export function sessionSkillsLine(setup) {
  if (!setup) return "Session skills: unknown (could not read them)";
  if (setup.mode === "installed") return "Session skills: installed (sessions load them from the environment's setup script)";
  if (setup.mode === "out-of-date") {
    return "Session skills: out of date, so sessions get the inline text. Paste the new setup script (Settings, Session skills) and confirm.";
  }
  return "Session skills: inline (no setup script confirmed)";
}

/** The one-line warning for `up` when the pasted setup script is stale, else null. */
export function sessionSkillsWarning(setup) {
  return setup?.mode === "out-of-date" ? sessionSkillsLine(setup) : null;
}

/** What `up` prints about session skills for the server at `url`: the out-of-date line, or null (also when it can't be read). */
export async function staleSessionSkillsWarning(url, fetchImpl = fetch) {
  return sessionSkillsWarning(await readSessionSkills(url, fetchImpl));
}
