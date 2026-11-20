import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { parseSeed, parseLegacySeed, hashPersona, personaIds, HISTORY_FILE } from "./seed-history.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const skill = (id, body) =>
  `---\nname: ${id}\ndescription: "Fyrd persona: x"\nkind: persona\npersona-name: Builder\npersona-model: claude-sonnet-5-5\npersona-locked: false\n---\n${body}`;

test("parseSeed reads a persona skill's front matter and trims the body", () => {
  assert.deepEqual(parseSeed("builder", skill("builder", "Hello\n\n")), { name: "Builder", model: "claude-sonnet-5-5", definition: "Hello" });
});

test("parseSeed skips a skill that is no persona, or whose name is not its directory's", () => {
  assert.equal(parseSeed("build", "---\nname: build\ndescription: \"d\"\nkind: controller-skill\n---\nx"), null);
  assert.equal(parseSeed("other", skill("builder", "x")), null);
});

test("parseLegacySeed reads a personas/hub file as it was", () => {
  assert.deepEqual(parseLegacySeed("builder", "---\nname: Builder\nmodel: claude-sonnet-5-5\nlocked: false\n---\nHello\n\n"),
    { name: "Builder", model: "claude-sonnet-5-5", definition: "Hello" });
});

test("a persona hashes the same before and after it became a skill", () => {
  assert.equal(hashPersona(parseSeed("builder", skill("builder", "Hello"))),
    hashPersona(parseLegacySeed("builder", "---\nname: Builder\nmodel: claude-sonnet-5-5\nlocked: false\n---\nHello")));
});

test("hashPersona is sha256 of name, model and definition joined by newlines", () => {
  // Known vector: sha256("A\nm\nd"), the same value HubPersonasTests asserts for HubPersonas.Hash.
  assert.equal(hashPersona({ name: "A", model: "m", definition: "d" }), "22319754cb8179c5f39718c645c51fbf82767867aedf0db954c8d94497577495");
  assert.notEqual(hashPersona({ name: "A", model: "m", definition: "d" }), hashPersona({ name: "A", model: "m", definition: "e" }));
});

test("parseSeed hashes CRLF and LF text the same", () => {
  const lf = skill("b", "One\nTwo\n");
  assert.equal(hashPersona(parseSeed("b", lf)), hashPersona(parseSeed("b", lf.replace(/\n/g, "\r\n"))));
});

test("the history file contains the hash of every current persona, with valid hashes", () => {
  const history = JSON.parse(fs.readFileSync(path.join(root, HISTORY_FILE), "utf8"));
  const ids = personaIds(root);
  assert.deepEqual(ids, ["builder", "controller", "investigator", "monitor", "researcher", "reviewer", "tuner", "validator"]);
  assert.deepEqual(Object.keys(history).sort(), ids);
  for (const id of ids) {
    const h = hashPersona(parseSeed(id, fs.readFileSync(path.join(root, "skills", id, "SKILL.md"), "utf8")));
    assert.ok(history[id].includes(h), `${id}: ${HISTORY_FILE} is stale; run node scripts/seed-history.mjs and commit it`);
    for (const x of history[id]) assert.match(x, /^[0-9a-f]{64}$/);
  }
});
