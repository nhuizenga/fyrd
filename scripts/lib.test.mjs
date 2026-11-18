import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import net from "node:net";
import { readServerFile, writeServerFile, isServerAlive, findFreePort, needsConsoleBuild, tokenWarning, gitHubLine, statusLine, readHubState, readSessionSkills, sessionSkillsLine, sessionSkillsWarning, staleSessionSkillsWarning } from "./lib.mjs";
import http from "node:http";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

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
  assert.equal(sessionSkillsLine({ mode: "inline" }), "Session skills: inline (no setup script confirmed)");
  assert.equal(sessionSkillsLine({ mode: "installed" }), "Session skills: installed (sessions load them from the environment's setup script)");
  assert.equal(sessionSkillsLine({ mode: "out-of-date" }),
    "Session skills: out of date, so sessions get the inline text. Paste the new setup script (Settings, Session skills) and confirm.");
  assert.equal(sessionSkillsLine(null), "Session skills: unknown (could not read them)");
  for (const mode of ["inline", "installed", "out-of-date"]) assert.ok(!sessionSkillsLine({ mode }).includes("\n"));
});

test("sessionSkillsWarning speaks only when the pasted script is out of date", () => {
  assert.equal(sessionSkillsWarning({ mode: "inline" }), null);
  assert.equal(sessionSkillsWarning({ mode: "installed" }), null);
  assert.equal(sessionSkillsWarning(null), null);
  assert.match(sessionSkillsWarning({ mode: "out-of-date" }), /^Session skills: out of date.*Paste the new setup script/);
});

test("readSessionSkills reads the setup-script state, and gives null when it can't", async () => {
  const urls = [];
  const setup = { mode: "installed", hash: "h" };
  const okFetch = async (url, init) => { urls.push(url); assert.ok(init.signal); return { ok: true, json: async () => setup }; };
  assert.deepEqual(await readSessionSkills("http://127.0.0.1:4870", okFetch), setup);
  assert.deepEqual(urls, ["http://127.0.0.1:4870/api/hub/session-skills/setup-script"]);
  assert.equal(await readSessionSkills("http://x", async () => ({ ok: false, json: async () => ({}) })), null);
  assert.equal(await readSessionSkills("http://x", async () => { throw new Error("timeout"); }), null);
  assert.equal(await readSessionSkills("http://x", async () => ({ ok: true, json: async () => { throw new SyntaxError("not json"); } })), null);
});

test("staleSessionSkillsWarning reads the state and speaks only when it is out of date", async () => {
  const fetchOf = (mode) => async () => ({ ok: true, json: async () => ({ mode }) });
  assert.match(await staleSessionSkillsWarning("http://x", fetchOf("out-of-date")), /^Session skills: out of date/);
  assert.equal(await staleSessionSkillsWarning("http://x", fetchOf("installed")), null);
  assert.equal(await staleSessionSkillsWarning("http://x", async () => { throw new Error("down"); }), null);
});

// `up` with a server already running: the out-of-date line is printed on that path too, not only after a start.
test("up says that the pasted script is out of date when Fyrd is already running", async () => {
  let mode = "out-of-date";
  const server = http.createServer((req, res) => {
    res.setHeader("content-type", "application/json");
    res.end(JSON.stringify(req.url === "/api/health" ? { status: "ok" } : { mode }));
  });
  await new Promise((r) => server.listen(0, "127.0.0.1", r));
  const home = fs.mkdtempSync(path.join(os.tmpdir(), "fyrd-up-"));
  try {
    writeServerFile(path.join(home, ".fyrd", "run"), { pid: process.pid, port: server.address().port, startedAt: new Date().toISOString() });
    const up = () => new Promise((resolve, reject) => {
      // Only what the script needs: no token of the caller's reaches it, and no browser opens.
      const child = spawn(process.execPath, [fileURLToPath(new URL("./fyrd.mjs", import.meta.url)), "up"], {
        env: { PATH: process.env.PATH, HOME: home, USERPROFILE: home, FYRD_NO_BROWSER: "1" },
      });
      let out = "";
      child.stdout.on("data", (d) => { out += d; });
      child.stderr.on("data", (d) => { out += d; });
      child.on("error", reject);
      child.on("close", (code) => resolve({ code, out }));
    });
    const stale = await up();
    assert.equal(stale.code, 0, stale.out);
    assert.deepEqual(stale.out.trimEnd().split("\n"), [
      `Fyrd already running at http://127.0.0.1:${server.address().port}`,
      "Session skills: out of date, so sessions get the inline text. Paste the new setup script (Settings, Session skills) and confirm.",
    ]);
    mode = "installed";
    assert.deepEqual((await up()).out.trimEnd().split("\n"), [`Fyrd already running at http://127.0.0.1:${server.address().port}`]);
  } finally {
    server.close();
    fs.rmSync(home, { recursive: true, force: true });
  }
});
