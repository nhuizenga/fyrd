import { spawnSync } from "node:child_process";
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

/** The `status` line for session skills: which mode sessions get them in, and which skills stay inline because they were edited. */
export function sessionSkillsLine(setup) {
  if (!setup) return "Session skills: unknown (could not read them)";
  if (setup.mode === "installed") {
    const inline = setup.inlineSkills ?? [];
    return "Session skills: installed (sessions load the defaults from the fyrd plugin)"
      + (inline.length ? `; inline because edited or added in the console: ${inline.join(", ")}` : "");
  }
  return "Session skills: inline (the plugin setup script is not confirmed)";
}

/**
 * How `up` starts the server. In a clone (runtime/src/Fyrd.Server is there) it builds the console when needed, builds the
 * server and runs the build output from the project folder, so wwwroot is found. In the release's plugin zip, which has
 * no runtime source, it runs the `fyrd` dotnet tool from the same release (see docs/releases.md) and points it at the
 * plugin's own skills and docs, so the skills the console serves are the ones Claude Code loads.
 */
export function serverLaunch(repoRoot, port, exists = fs.existsSync) {
  const urls = ["--urls", `http://127.0.0.1:${port}`];
  if (exists(path.join(repoRoot, "runtime/src/Fyrd.Server/Fyrd.Server.csproj"))) {
    return {
      source: true,
      cmd: "dotnet",
      args: [path.join(repoRoot, "runtime/src/Fyrd.Server/bin/Release/net10.0/Fyrd.Server.dll"), ...urls],
      cwd: path.join(repoRoot, "runtime/src/Fyrd.Server"),
    };
  }
  const skills = path.join(repoRoot, "skills");
  return {
    source: false,
    cmd: "fyrd",
    args: [...urls, `--Fyrd:HubPersonasSeedDir=${skills}`, `--Fyrd:HubSkillsDir=${skills}`,
      `--Fyrd:SessionSkillsSeedDir=${skills}`, `--Fyrd:DocsDir=${path.join(repoRoot, "docs")}`],
    cwd: repoRoot,
  };
}

/** The plugin's version, from .claude-plugin/plugin.json under `repoRoot`. Throws unless it is MAJOR.MINOR.PATCH. */
export function pluginVersion(repoRoot, read = fs.readFileSync) {
  const file = path.join(repoRoot, ".claude-plugin", "plugin.json");
  const version = JSON.parse(read(file, "utf8")).version;
  if (typeof version !== "string" || !/^[0-9]+\.[0-9]+\.[0-9]+$/.test(version)) {
    throw new Error(`The version in ${file} must be MAJOR.MINOR.PATCH, not ${JSON.stringify(version)}.`);
  }
  return version;
}

/**
 * The commands that install the fyrd tool at `version`, or move an installed one to it, from a folder holding the
 * release's package. --source replaces every configured package source with that folder, so a `fyrd` package on
 * nuget.org or any other feed can never be picked instead. docs/releases.md shows the same commands (a test checks).
 */
export function toolInstallCommands(version, folder = `<folder with fyrd.${version}.nupkg>`) {
  const args = `fyrd --version ${version} --source ${folder}`;
  return {
    install: `dotnet tool install --global ${args}`,
    update: `dotnet tool update --global ${args} --allow-downgrade`,
  };
}

const downloadStep = (version) =>
  `Download fyrd.${version}.nupkg from GitHub Release v${version} of the fyrd repository into a folder of its own`;

/** What `up` prints when the plugin has no runtime source and no `fyrd` tool is on PATH or in ~/.dotnet/tools. */
export function toolMissing(version) {
  return `The fyrd console tool is not installed (no fyrd on PATH or in ~/.dotnet/tools). This plugin is ${version}, `
    + `so install the tool from the same release. ${downloadStep(version)}, then run:\n  ${toolInstallCommands(version).install}\n`
    + "See docs/releases.md.";
}

/**
 * Checks that `cmd` is the Fyrd console tool at the plugin's `expected` version, by running `cmd --version` (which the
 * server answers with `fyrd <version>` and exits, see runtime/src/Fyrd.Server/ServerVersion.cs) before `up` starts it.
 * Returns { ok: true } or { ok: false, message } with what is wrong and how to fix it.
 */
export function checkTool(cmd, expected, run = spawnSync) {
  const r = run(cmd, ["--version"], { encoding: "utf8", timeout: 15000, windowsHide: true });
  const out = String(r.stdout ?? "").trim();
  const m = r.error || r.status !== 0 ? null : /^fyrd ([0-9]+\.[0-9]+\.[0-9]+)$/.exec(out);
  if (!m) {
    const answer = r.error ? `failed (${r.error.message})`
      : r.status !== 0 ? `exited with ${r.status ?? r.signal}` : `answered ${JSON.stringify(out.split(/\r?\n/)[0].slice(0, 80))}`;
    return {
      ok: false,
      message: `Refusing to start ${cmd}: it is not the Fyrd console tool (\`${path.basename(cmd)} --version\` ${answer}, `
        + `not "fyrd ${expected}"). Remove it from PATH or rename it, then install the tool: ${downloadStep(expected)}, then run:\n  `
        + `${toolInstallCommands(expected).install}\nSee docs/releases.md.`,
    };
  }
  if (m[1] !== expected) {
    return {
      ok: false,
      message: `Refusing to start ${cmd}: it is the fyrd tool ${m[1]}, but this plugin is ${expected}, and the tool must come `
        + `from the same release. ${downloadStep(expected)}, then run:\n  ${toolInstallCommands(expected).update}\nSee docs/releases.md.`,
    };
  }
  return { ok: true };
}

/**
 * The full path of `cmd` on PATH (with PATHEXT on Windows), else of the global dotnet tool in ~/.dotnet/tools (under
 * DOTNET_CLI_HOME when set), where `dotnet tool install --global` puts it even when that folder is not on PATH yet;
 * else null.
 */
export function findOnPath(cmd, env = process.env, platform = process.platform, exists = fs.existsSync, home = os.homedir()) {
  const win = platform === "win32";
  const p = win ? path.win32 : path.posix;
  const exts = win ? (env.PATHEXT || ".COM;.EXE;.BAT;.CMD").split(";").filter(Boolean) : [""];
  for (const d of (env.PATH || env.Path || "").split(win ? ";" : ":").filter(Boolean)) {
    for (const ext of exts) {
      const candidate = p.join(d, cmd + ext);
      if (exists(candidate)) return candidate;
    }
  }
  const tool = p.join(env.DOTNET_CLI_HOME || home, ".dotnet", "tools", cmd + (win ? ".exe" : ""));
  return exists(tool) ? tool : null;
}
