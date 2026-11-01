#!/usr/bin/env node
// Generates personas/hub/seed-history.json: for each persona seed, the hash of every committed version.
// HubPersonas uses it to recognise legacy installs (no SeedHash) whose persona was never edited.
// The hash must match HubPersonas.Hash: sha256 of name + "\n" + model + "\n" + definition.
import { execFileSync } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const FRONT = /^---[ \t]*\r?\n([\s\S]*?)\r?\n---[ \t]*\r?\n?([\s\S]*)$/;

export function parseSeed(id, text) {
  const m = FRONT.exec(text);
  if (!m) return null;
  let name = id;
  let model = "claude-sonnet-5-5";
  for (const line of m[1].split("\n")) {
    const i = line.indexOf(":");
    if (i < 0) continue;
    const key = line.slice(0, i).trim();
    const val = line.slice(i + 1).trim();
    if (key === "name") name = val;
    else if (key === "model") model = val;
  }
  // CRLF normalised, as HubPersonas.ParseSeed does.
  return { name, model, definition: m[2].replace(/\r\n/g, "\n").trim() };
}

export function hashPersona({ name, model, definition }) {
  return crypto.createHash("sha256").update(`${name}\n${model}\n${definition}`, "utf8").digest("hex");
}

export function buildHistory(repoRoot, personasDir = "personas/hub") {
  const git = (...args) => execFileSync("git", args, { cwd: repoRoot, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  const dir = path.join(repoRoot, personasDir);
  const out = {};
  for (const file of fs.readdirSync(dir).filter((f) => f.endsWith(".md")).sort()) {
    const id = file.slice(0, -3);
    const rel = `${personasDir}/${file}`;
    const hashes = new Set();
    for (const sha of git("log", "--format=%H", "--", rel).split("\n").filter(Boolean)) {
      let text;
      try { text = git("show", `${sha}:${rel}`); } catch { continue; }
      const seed = parseSeed(id, text);
      if (seed) hashes.add(hashPersona(seed));
    }
    out[id] = [...hashes].sort();
  }
  return out;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const history = buildHistory(root);
  fs.writeFileSync(path.join(root, "personas/hub/seed-history.json"), JSON.stringify(history, null, 2) + "\n");
  console.log(`wrote personas/hub/seed-history.json (${Object.keys(history).length} personas)`);
}
