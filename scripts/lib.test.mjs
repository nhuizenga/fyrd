import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import net from "node:net";
import { readServerFile, writeServerFile, isServerAlive, findFreePort, needsConsoleBuild, tokenWarning, gitHubLine, statusLine, readHubState, readSessionSkills, sessionSkillsLine, serverLaunch, toolMissing, findOnPath, pluginVersion, toolInstallCommands, checkTool } from "./lib.mjs";

const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), "fyrd-test-"));
const ok = async () => ({ ok: true, json: async () => ({ status: "ok" }) });

test("readServerFile returns null for malformed json", () => {
  const d = tmp();
  fs.writeFileSync(path.join(d, "server.json"), "{nope");
  assert.equal(readServerFile(d), null);
  assert.equal(readServerFile(path.join(d, "missing")), null);
});

test("writeServerFile round-trips", () => {
  const d = tmp();
  const info = { pid: 1, port: 2, startedAt: "x" };
  writeServerFile(d, info);
  assert.deepEqual(readServerFile(d), info);
});

test("isServerAlive false when pid is dead", async () => {
  assert.equal(await isServerAlive({ pid: 999999, port: 4870 }, ok), false);
});

test("isServerAlive false when health fails", async () => {
  const f = async () => { throw new Error("down"); };
  assert.equal(await isServerAlive({ pid: process.pid, port: 4870 }, f), false);
});

test("isServerAlive true when pid alive and health ok", async () => {
  assert.equal(await isServerAlive({ pid: process.pid, port: 4870 }, ok), true);
});

function occupy(port) {
  return new Promise((res, rej) => {
    const s = net.createServer();
    s.once("error", rej);
    s.listen(port, "127.0.0.1", () => res(s));
  });
}

test("findFreePort skips busy port", async () => {
  const s = await occupy(48870);
  try { assert.equal(await findFreePort(48870, 10), 48871); }
  finally { s.close(); }
});

test("findFreePort throws when all busy", async () => {
  const s = await occupy(48885);
  try { await assert.rejects(findFreePort(48885, 1), /No free port/); }
  finally { s.close(); }
});

test("needsConsoleBuild true when wwwroot missing", () => {
  assert.equal(needsConsoleBuild(tmp()), true);
});

test("needsConsoleBuild false when index newer than sources", () => {
  const r = tmp();
  fs.mkdirSync(path.join(r, "console/src"), { recursive: true });
  fs.writeFileSync(path.join(r, "console/src/a.tsx"), "x");
  const w = path.join(r, "runtime/src/Fyrd.Server/wwwroot");
  fs.mkdirSync(w, { recursive: true });
  fs.writeFileSync(path.join(w, "index.html"), "x");
  const past = new Date(Date.now() - 10000);
  fs.utimesSync(path.join(r, "console/src/a.tsx"), past, past);
  assert.equal(needsConsoleBuild(r), false);
});

test("tokenWarning says GH_TOKEN is not set when neither variable is", () => {
  assert.equal(
    tokenWarning({}),
    "GH_TOKEN is not set: PR status and PR actions are off. Set it and run /fyrd:down then /fyrd:up.",
  );
  assert.equal(tokenWarning({ GH_TOKEN: "" , GITHUB_TOKEN: "" }) !== null, true);
});

test("tokenWarning is silent with GH_TOKEN or GITHUB_TOKEN", () => {
  assert.equal(tokenWarning({ GH_TOKEN: "x" }), null);
  assert.equal(tokenWarning({ GITHUB_TOKEN: "x" }), null);
});

test("statusLine counts sessions and waiting questions, or gives only the URL", () => {
  const url = "http://127.0.0.1:4870";
  assert.equal(statusLine(url, { sessions: [{}, {}], questions: [{}] }), `Fyrd running at ${url} · 2 sessions · 1 question waiting`);
  assert.equal(statusLine(url, { sessions: [{}], questions: [{}, {}] }), `Fyrd running at ${url} · 1 session · 2 questions waiting`);
  assert.equal(statusLine(url, { sessions: [], questions: [] }), `Fyrd running at ${url} · 0 sessions · 0 questions waiting`);
  assert.equal(statusLine(url, {}), `Fyrd running at ${url} · 0 sessions · 0 questions waiting`);
  assert.equal(statusLine("http://127.0.0.1:4870", null), "Fyrd running at http://127.0.0.1:4870");
});

test("readHubState returns the state on 2xx and null on a non-2xx JSON body, bad JSON or a failed fetch", async () => {
  const url = "http://127.0.0.1:4870";
  const state = { sessions: [{}], questions: [] };
  let asked;
  assert.deepEqual(await readHubState(url, async (u) => { asked = u; return { ok: true, status: 200, json: async () => state }; }), state);
  assert.equal(asked, `${url}/api/hub/state`);
  // A ProblemDetails 500 is valid JSON; it must not be read as an empty hub state.
  const problem = async () => ({ ok: false, status: 500, json: async () => ({ title: "An error occurred", status: 500 }) });
  assert.equal(await readHubState(url, problem), null);
  assert.equal(statusLine(url, await readHubState(url, problem)), `Fyrd running at ${url}`);
  assert.equal(await readHubState(url, async () => ({ ok: true, status: 200, json: async () => { throw new SyntaxError("bad json"); } })), null);
  assert.equal(await readHubState(url, async () => { throw new Error("ECONNREFUSED"); }), null);
});

test("readHubState gives up on a stalled server: it passes an abort signal, and an aborted fetch gives null", async () => {
  let signal;
  const stalled = (_u, init) => new Promise((_res, rej) => {
    signal = init?.signal;
    // Stands in for the 3 s timeout: reject the way fetch does when its signal aborts.
    setTimeout(() => rej(new DOMException("The operation was aborted due to timeout", "TimeoutError")), 5);
  });
  assert.equal(await readHubState("http://127.0.0.1:4870", stalled), null);
  assert.ok(signal instanceof AbortSignal);
  assert.equal(signal.aborted, false);
});

test("gitHubLine names the login, the error, or says not configured", () => {
  assert.equal(gitHubLine({ configured: true, login: "octocat", error: null }), "GitHub: signed in as octocat");
  assert.equal(gitHubLine({ configured: false, login: null, error: null }), "GitHub: not configured");
  assert.equal(gitHubLine(undefined), "GitHub: not configured");
  assert.match(gitHubLine({ configured: true, login: null, error: "bad or expired" }), /bad or expired/);
});

test("sessionSkillsLine names the mode in one line", () => {
  assert.equal(sessionSkillsLine({ mode: "inline" }), "Session skills: inline (the plugin setup script is not confirmed)");
  assert.equal(sessionSkillsLine({ mode: "installed" }), "Session skills: installed (sessions load the defaults from the fyrd plugin)");
  assert.equal(sessionSkillsLine({ mode: "installed", inlineSkills: [] }), "Session skills: installed (sessions load the defaults from the fyrd plugin)");
  assert.equal(sessionSkillsLine({ mode: "installed", inlineSkills: ["stacked-pull-requests", "mine"] }),
    "Session skills: installed (sessions load the defaults from the fyrd plugin); inline because edited or added in the console: stacked-pull-requests, mine");
  assert.equal(sessionSkillsLine(null), "Session skills: unknown (could not read them)");
  for (const mode of ["inline", "installed"]) assert.ok(!sessionSkillsLine({ mode, inlineSkills: ["a"] }).includes("\n"));
});

test("readSessionSkills reads the setup-script state, and gives null when it can't", async () => {
  const urls = [];
  const setup = { mode: "installed", confirmed: true };
  const okFetch = async (url, init) => { urls.push(url); assert.ok(init.signal); return { ok: true, json: async () => setup }; };
  assert.deepEqual(await readSessionSkills("http://127.0.0.1:4870", okFetch), setup);
  assert.deepEqual(urls, ["http://127.0.0.1:4870/api/hub/session-skills/setup-script"]);
  assert.equal(await readSessionSkills("http://x", async () => ({ ok: false, json: async () => ({}) })), null);
  assert.equal(await readSessionSkills("http://x", async () => { throw new Error("timeout"); }), null);
  assert.equal(await readSessionSkills("http://x", async () => ({ ok: true, json: async () => { throw new SyntaxError("not json"); } })), null);
});


test("serverLaunch runs the build output from the project folder in a clone", () => {
  const root = path.join("r");
  const l = serverLaunch(root, 4871, (p) => p === path.join(root, "runtime/src/Fyrd.Server/Fyrd.Server.csproj"));
  assert.equal(l.source, true);
  assert.equal(l.cmd, "dotnet");
  assert.deepEqual(l.args, [path.join(root, "runtime/src/Fyrd.Server/bin/Release/net10.0/Fyrd.Server.dll"), "--urls", "http://127.0.0.1:4871"]);
  assert.equal(l.cwd, path.join(root, "runtime/src/Fyrd.Server"));
});

test("serverLaunch runs the fyrd tool with the plugin's skills and docs when there is no runtime source", () => {
  const root = path.join("plugin");
  const l = serverLaunch(root, 4870, () => false);
  const skills = path.join(root, "skills");
  assert.equal(l.source, false);
  assert.equal(l.cmd, "fyrd");
  assert.deepEqual(l.args, ["--urls", "http://127.0.0.1:4870", `--Fyrd:HubPersonasSeedDir=${skills}`,
    `--Fyrd:HubSkillsDir=${skills}`, `--Fyrd:SessionSkillsSeedDir=${skills}`, `--Fyrd:DocsDir=${path.join(root, "docs")}`]);
  assert.equal(l.cwd, root);
});

test("pluginVersion reads plugin.json, and refuses a version that is not MAJOR.MINOR.PATCH", () => {
  const read = (v) => () => JSON.stringify({ name: "fyrd", version: v });
  assert.equal(pluginVersion("r", read("0.2.57")), "0.2.57");
  assert.throws(() => pluginVersion("r", read("0.2.57\n")), /MAJOR\.MINOR\.PATCH/);
  assert.throws(() => pluginVersion("r", read(undefined)), /MAJOR\.MINOR\.PATCH/);
  const repo = path.resolve(path.dirname(new URL(import.meta.url).pathname), "..");
  assert.match(pluginVersion(repo), /^[0-9]+\.[0-9]+\.[0-9]+$/);
});

test("toolInstallCommands installs and updates from the release folder only", () => {
  const c = toolInstallCommands("1.2.3", "/dl");
  assert.equal(c.install, "dotnet tool install --global fyrd --version 1.2.3 --source /dl");
  assert.equal(c.update, "dotnet tool update --global fyrd --version 1.2.3 --source /dl --allow-downgrade");
  assert.doesNotMatch(c.install + c.update, /--add-source/);
});

test("docs/releases.md shows the same install and update commands", () => {
  const docs = fs.readFileSync(new URL("../docs/releases.md", import.meta.url), "utf8");
  const c = toolInstallCommands("<version>", "<folder>");
  assert.ok(docs.includes(c.install), `docs/releases.md lacks: ${c.install}`);
  assert.ok(docs.includes(c.update), `docs/releases.md lacks: ${c.update}`);
});

test("toolMissing names the plugin version and the real install command", () => {
  const m = toolMissing("1.2.3");
  assert.match(m, /This plugin is 1\.2\.3/);
  assert.match(m, /fyrd\.1\.2\.3\.nupkg/);
  assert.match(m, /GitHub Release v1\.2\.3/);
  assert.ok(m.includes(toolInstallCommands("1.2.3").install));
  assert.doesNotMatch(m, /<version>/);
});

const answers = (r) => { const calls = []; const run = (cmd, args, opts) => { calls.push({ cmd, args, opts }); return r; }; return { run, calls }; };

test("checkTool accepts the Fyrd tool at the plugin version, asking it with --version", () => {
  const { run, calls } = answers({ status: 0, stdout: "fyrd 1.2.3\r\n" });
  assert.deepEqual(checkTool("/t/fyrd", "1.2.3", run), { ok: true });
  assert.equal(calls[0].cmd, "/t/fyrd");
  assert.deepEqual(calls[0].args, ["--version"]);
  assert.ok(calls[0].opts.timeout > 0);
});

test("checkTool refuses the Fyrd tool at another version, with the update command", () => {
  const r = checkTool("/t/fyrd", "1.2.3", answers({ status: 0, stdout: "fyrd 1.2.2\n" }).run);
  assert.equal(r.ok, false);
  assert.match(r.message, /it is the fyrd tool 1\.2\.2, but this plugin is 1\.2\.3/);
  assert.ok(r.message.includes(toolInstallCommands("1.2.3").update));
});

test("checkTool refuses a fyrd that is not the Fyrd tool", () => {
  for (const [res, why] of [
    [{ status: 0, stdout: "fyrd version 3.0 (some other fyrd)\n" }, /answered "fyrd version 3\.0/],
    [{ status: 0, stdout: "fyrd 1.2.3\nextra\n" }, /answered "fyrd 1\.2\.3"/],
    [{ status: 2, stdout: "fyrd 1.2.3\n" }, /exited with 2/],
    [{ status: null, signal: "SIGTERM", stdout: "" }, /exited with SIGTERM/],
    [{ error: new Error("spawn EACCES"), stdout: "" }, /failed \(spawn EACCES\)/],
  ]) {
    const r = checkTool("/t/fyrd", "1.2.3", answers(res).run);
    assert.equal(r.ok, false);
    assert.match(r.message, /Refusing to start \/t\/fyrd: it is not the Fyrd console tool/);
    assert.match(r.message, why);
    assert.ok(r.message.includes(toolInstallCommands("1.2.3").install));
  }
});

test("findOnPath finds a command on POSIX PATH and returns null when absent", () => {
  const env = { PATH: "/a:/b" };
  assert.equal(findOnPath("fyrd", env, "linux", (p) => p === "/b/fyrd", "/home/u"), "/b/fyrd");
  assert.equal(findOnPath("fyrd", env, "linux", () => false, "/home/u"), null);
  assert.equal(findOnPath("fyrd", {}, "linux", () => false, "/home/u"), null);
});

test("findOnPath prefers PATH, then falls back to ~/.dotnet/tools", () => {
  const env = { PATH: "/a" };
  assert.equal(findOnPath("fyrd", env, "linux", (p) => p === "/home/u/.dotnet/tools/fyrd", "/home/u"), "/home/u/.dotnet/tools/fyrd");
  assert.equal(findOnPath("fyrd", env, "linux", () => true, "/home/u"), "/a/fyrd");
  assert.equal(findOnPath("fyrd", { DOTNET_CLI_HOME: "/cli" }, "linux", (p) => p === "/cli/.dotnet/tools/fyrd", "/home/u"), "/cli/.dotnet/tools/fyrd");
  assert.equal(findOnPath("fyrd", { Path: "C:\\t" }, "win32", (p) => p === "C:\\Users\\u\\.dotnet\\tools\\fyrd.exe", "C:\\Users\\u"),
    "C:\\Users\\u\\.dotnet\\tools\\fyrd.exe");
});
test("findOnPath tries PATHEXT on Windows", () => {
  const env = { Path: "C:\\t;C:\\u", PATHEXT: ".COM;.EXE" };
  assert.equal(findOnPath("fyrd", env, "win32", (p) => p === "C:\\u\\fyrd.EXE"), "C:\\u\\fyrd.EXE");
});
