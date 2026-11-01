import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { parseSeed, hashPersona } from "./seed-history.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

test("parseSeed reads front matter and trims the body", () => {
  const s = parseSeed("builder", "---\nname: Builder\nmodel: claude-sonnet-5-5\nlocked: false\n---\nHello\n\n");
  assert.deepEqual(s, { name: "Builder", model: "claude-sonnet-5-5", definition: "Hello" });
});

test("hashPersona is sha256 of name, model and definition joined by newlines", () => {
  // Known vector: sha256("A\nm\nd"), the same value HubPersonasTests asserts for HubPersonas.Hash.
  assert.equal(hashPersona({ name: "A", model: "m", definition: "d" }), "22319754cb8179c5f39718c645c51fbf82767867aedf0db954c8d94497577495");
  assert.match(hashPersona({ name: "A", model: "m", definition: "d" }), /^[0-9a-f]{64}$/);
  assert.notEqual(hashPersona({ name: "A", model: "m", definition: "d" }), hashPersona({ name: "A", model: "m", definition: "e" }));
});

test("parseSeed hashes CRLF and LF text the same", () => {
  const lf = "---\nname: B\nmodel: m\n---\nOne\nTwo\n";
  assert.equal(hashPersona(parseSeed("b", lf)), hashPersona(parseSeed("b", lf.replace(/\n/g, "\r\n"))));
});

test("seed-history.json contains the hash of every current persona", () => {
  const dir = path.join(root, "personas/hub");
  const history = JSON.parse(fs.readFileSync(path.join(dir, "seed-history.json"), "utf8"));
  for (const f of fs.readdirSync(dir).filter((f) => f.endsWith(".md"))) {
    const id = f.slice(0, -3);
    const h = hashPersona(parseSeed(id, fs.readFileSync(path.join(dir, f), "utf8")));
    assert.ok(history[id]?.includes(h), `${id}: seed-history.json is stale; run node scripts/seed-history.mjs and commit it`);
  }
});

test("seed-history.json lists every persona with valid hashes", () => {
  const dir = path.join(root, "personas/hub");
  const history = JSON.parse(fs.readFileSync(path.join(dir, "seed-history.json"), "utf8"));
  for (const f of fs.readdirSync(dir).filter((f) => f.endsWith(".md"))) {
    const id = f.slice(0, -3);
    assert.ok(Array.isArray(history[id]) && history[id].length > 0, `${id} has no history`);
    for (const h of history[id]) assert.match(h, /^[0-9a-f]{64}$/);
  }
});
