#!/usr/bin/env node
// Generates skills/persona-seed-history.json: for each persona seed, the hash of every committed version.
// HubPersonas uses it to recognise legacy installs (no SeedHash) whose persona was never edited.
// The hash must match HubPersonas.Hash: sha256 of name + "\n" + model + "\n" + definition.
// A persona seed is skills/<id>/SKILL.md with `kind: persona`; before that it was personas/hub/<id>.md, whose
// versions are read too. Hashes already in the file are kept, so a shallow clone never drops one.
import { execFileSync } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const FRONT = /^---[ \t]*\r?\n([\s\S]*?)\r?\n---[ \t]*\r?\n?([\s\S]*)$/;
export const HISTORY_FILE = "skills/persona-seed-history.json";
const LEGACY_DIR = "personas/hub";

function unquote(v) {
  if (v.length < 2 || v[0] !== '"' || v[v.length - 1] !== '"') return v;
  return v.slice(1, -1).replace(/\\(["\\])/g, "$1");
}

function frontMatter(text) {
  const m = FRONT.exec(text);
  if (!m) return null;
  const fields = {};
  for (const line of m[1].split("\n")) {
    const i = line.indexOf(":");
    if (i < 0) continue;
    fields[line.slice(0, i).trim()] = unquote(line.slice(i + 1).trim());
  }
  // CRLF normalised, as HubPersonas.ParseSeed does.
  return { fields, body: m[2].replace(/\r\n/g, "\n").trim() };
}

/** A persona skill (skills/<id>/SKILL.md, `kind: persona`), as HubPersonas.ParseSeed reads it; null for any other file. */
export function parseSeed(id, text) {
  const fm = frontMatter(text);
  if (!fm || fm.fields.kind !== "persona" || fm.fields.name !== id) return null;
  return { name: fm.fields["persona-name"] ?? id, model: fm.fields["persona-model"] ?? "claude-sonnet-5-5", definition: fm.body };
}

/** A persona as personas/hub/<id>.md held it, before personas became skills. */
export function parseLegacySeed(id, text) {
  const fm = frontMatter(text);
  if (!fm) return null;
  return { name: fm.fields.name ?? id, model: fm.fields.model ?? "claude-sonnet-5-5", definition: fm.body };
}

export function hashPersona({ name, model, definition }) {
  return crypto.createHash("sha256").update(`${name}\n${model}\n${definition}`, "utf8").digest("hex");
}

/** The ids of the persona skills in the working tree, sorted. */
export function personaIds(repoRoot) {
  const dir = path.join(repoRoot, "skills");
  return fs.readdirSync(dir, { withFileTypes: true })
    .filter((d) => d.isDirectory() && fs.existsSync(path.join(dir, d.name, "SKILL.md")))
    .map((d) => d.name)
    .filter((id) => parseSeed(id, fs.readFileSync(path.join(dir, id, "SKILL.md"), "utf8")))
    .sort();
}

export function buildHistory(repoRoot) {
  // stderr is piped, not inherited: `git show` on a path a commit doesn't hold fails (caught below) and would print "fatal: …".
  const git = (...args) => execFileSync("git", args, { cwd: repoRoot, encoding: "utf8", maxBuffer: 64 * 1024 * 1024, stdio: ["ignore", "pipe", "pipe"] });
  const file = path.join(repoRoot, HISTORY_FILE);
  const existing = fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, "utf8")) : {};
  const out = {};
  for (const id of personaIds(repoRoot)) {
    const hashes = new Set(existing[id] ?? []);
    for (const [rel, parse] of [[`skills/${id}/SKILL.md`, parseSeed], [`${LEGACY_DIR}/${id}.md`, parseLegacySeed]]) {
      for (const sha of git("log", "--format=%H", "--", rel).split("\n").filter(Boolean)) {
        let text;
        try { text = git("show", `${sha}:${rel}`); } catch { continue; }
        const seed = parse(id, text);
        if (seed) hashes.add(hashPersona(seed));
      }
    }
    // The working tree's text too, so the file can be committed with the persona change it records.
    const seed = parseSeed(id, fs.readFileSync(path.join(repoRoot, "skills", id, "SKILL.md"), "utf8"));
    hashes.add(hashPersona(seed));
    out[id] = [...hashes].sort();
  }
  return out;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const history = buildHistory(root);
  fs.writeFileSync(path.join(root, HISTORY_FILE), JSON.stringify(history, null, 2) + "\n");
  console.log(`wrote ${HISTORY_FILE} (${Object.keys(history).length} personas)`);
}
