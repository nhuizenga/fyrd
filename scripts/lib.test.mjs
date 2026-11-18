import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import net from "node:net";
import { readServerFile, writeServerFile, isServerAlive, findFreePort, needsConsoleBuild, tokenWarning, gitHubLine, statusLine } from "./lib.mjs";

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
  assert.equal(statusLine("http://127.0.0.1:4870", { sessions: [{}, {}], questions: [{}] }),
    "Fyrd running at http://127.0.0.1:4870 · 2 sessions · 1 questions waiting");
  assert.equal(statusLine("http://127.0.0.1:4870", null), "Fyrd running at http://127.0.0.1:4870");
});

test("gitHubLine names the login, the error, or says not configured", () => {
  assert.equal(gitHubLine({ configured: true, login: "octocat", error: null }), "GitHub: signed in as octocat");
  assert.equal(gitHubLine({ configured: false, login: null, error: null }), "GitHub: not configured");
  assert.equal(gitHubLine(undefined), "GitHub: not configured");
  assert.match(gitHubLine({ configured: true, login: null, error: "bad or expired" }), /bad or expired/);
});
