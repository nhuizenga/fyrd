#!/usr/bin/env node
import { spawn, spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  runDir, readServerFile, writeServerFile, removeServerFile,
  isServerAlive, findFreePort, needsConsoleBuild, tokenWarning, gitHubLine, statusLine, readHubState,
  readSessionSkills, sessionSkillsLine, serverLaunch, toolMissing, findOnPath, pluginVersion, checkTool,
} from "./lib.mjs";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const dir = runDir();
const urlOf = (info) => `http://127.0.0.1:${info.port}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const win = process.platform === "win32";

function openBrowser(url) {
  if (process.env.FYRD_NO_BROWSER === "1") return;
  const [cmd, args] = win ? ["cmd", ["/c", "start", "", url]]
    : process.platform === "darwin" ? ["open", [url]] : ["xdg-open", [url]];
  spawn(cmd, args, { detached: true, stdio: "ignore", windowsHide: true }).unref();
}

function run(cmd, args, cwd) {
  const r = win
    ? spawnSync(`${cmd} ${args.join(" ")}`, { cwd, stdio: "inherit", shell: true })
    : spawnSync(cmd, args, { cwd, stdio: "inherit" });
  if (r.status !== 0) {
    console.error(`Command failed: ${cmd} ${args.join(" ")}`);
    process.exit(1);
  }
}

async function consoleServed(info) {
  try {
    return (await fetch(`${urlOf(info)}/`, { signal: AbortSignal.timeout(2000) })).status === 200;
  } catch {
    return false;
  }
}

function fail(info, logPath, message) {
  try { process.kill(info.pid); } catch {}
  const lines = fs.readFileSync(logPath, "utf8").split(/\r?\n/);
  console.error(`${message} Last log lines:\n${lines.slice(-20).join("\n")}`);
  process.exit(1);
}

async function up() {
  const existing = readServerFile(dir);
  if (existing && (await isServerAlive(existing))) {
    console.log(`Fyrd already running at ${urlOf(existing)}`);
    openBrowser(urlOf(existing));
    return;
  }
  removeServerFile(dir);

  const port = await findFreePort();
  const launch = serverLaunch(repoRoot, port);
  if (launch.source) {
    if (needsConsoleBuild(repoRoot)) {
      run("npm", ["ci"], path.join(repoRoot, "console"));
      run("npm", ["run", "build"], path.join(repoRoot, "console"));
    }
    run("dotnet", ["build", "runtime/src/Fyrd.Server", "-c", "Release"], repoRoot);
  } else {
    // The plugin zip has no runtime source: run the fyrd tool, but only once it has said it is Fyrd at this version.
    const version = pluginVersion(repoRoot);
    launch.cmd = findOnPath(launch.cmd);
    if (!launch.cmd) {
      console.error(toolMissing(version));
      process.exit(1);
    }
    const check = checkTool(launch.cmd, version);
    if (!check.ok) {
      console.error(check.message);
      process.exit(1);
    }
  }

  fs.mkdirSync(dir, { recursive: true });
  const logPath = path.join(dir, "server.log");
  const log = fs.openSync(logPath, "w");
  // From a clone the content root must be the project dir so wwwroot (the built console) is found regardless of caller
  // cwd; the dotnet tool finds its console beside its own assembly.
  const child = spawn(launch.cmd, launch.args, {
    cwd: launch.cwd,
    detached: true, stdio: ["ignore", log, log], windowsHide: true,
  });
  child.unref();
  const info = { pid: child.pid, port, startedAt: new Date().toISOString() };

  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    if (await isServerAlive(info)) {
      if (await consoleServed(info)) {
        writeServerFile(dir, info);
        console.log(`Fyrd running at ${urlOf(info)}`);
        if (tokenWarning()) console.log(tokenWarning());
        openBrowser(urlOf(info));
        return;
      }
      fail(info, logPath, "Fyrd API is up but the console is not served (GET / failed): console not built? Run `npm run build` in console/.");
    }
    await sleep(300);
  }
  fail(info, logPath, "Fyrd did not become healthy within 30s.");
}

async function down() {
  const info = readServerFile(dir);
  if (info && (await isServerAlive(info))) {
    process.kill(info.pid);
    removeServerFile(dir);
    console.log("Fyrd stopped.");
  } else {
    removeServerFile(dir);
    console.log("Fyrd was not running.");
  }
}

async function status() {
  const info = readServerFile(dir);
  if (!info || !(await isServerAlive(info))) {
    console.log("Fyrd is not running. Start it with /fyrd:up.");
    return;
  }
  const [state, skills] = await Promise.all([readHubState(urlOf(info)), readSessionSkills(urlOf(info))]);
  console.log(statusLine(urlOf(info), state));
  console.log(state ? gitHubLine(state.gitHub) : "GitHub: unknown (could not read hub state)");
  console.log(sessionSkillsLine(skills));
}

const cmds = { up, down, status };
const cmd = cmds[process.argv[2]];
if (!cmd) {
  console.error("Usage: fyrd.mjs <up|down|status>");
  process.exit(2);
}
await cmd();
