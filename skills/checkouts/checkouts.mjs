#!/usr/bin/env node
// Lists the git checkouts on this machine: each direct clone and each `git worktree` worktree, with its GitHub
// repo (owner/name), branch, upstream branch and path. Used by the fyrd:checkouts skill; read-only (it runs only
// `git rev-parse`, `git worktree list`, `git remote get-url` and `git config`).
//
//   node checkouts.mjs [--json] [root ...]
//
// With no roots it looks in the current directory, its parent, $HOME and /home/user, down to MAX_DEPTH levels.
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const MAX_DEPTH = 3;
const SKIP = new Set(["node_modules", "bin", "obj", "dist", "build", "target", "vendor", "Library"]);

function git(cwd, args) {
  try {
    return execFileSync("git", args, { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], timeout: 10000 }).trim();
  } catch {
    return null;
  }
}

/**
 * owner/name of a GitHub remote URL, or null when it isn't one. Knows https, ssh (`git@github.com:o/r.git`,
 * `ssh://git@github.com/o/r`) and the cloud session git proxy (`http://…@127.0.0.1:<port>/git/o/r`).
 */
export function parseRemote(url) {
  if (!url) return null;
  const u = url.trim();
  const patterns = [
    /^(?:https?|ssh|git):\/\/(?:[^@/]+@)?github\.com(?::\d+)?\/([^/]+)\/([^/]+?)(?:\.git)?\/?$/i,
    /^[^@/\s]+@github\.com:([^/]+)\/([^/]+?)(?:\.git)?\/?$/i,
    /^https?:\/\/(?:[^@/]+@)?(?:127\.0\.0\.1|localhost)(?::\d+)?\/git\/([^/]+)\/([^/]+?)(?:\.git)?\/?$/i,
  ];
  for (const p of patterns) {
    const m = p.exec(u);
    if (m && /^[A-Za-z0-9-]+$/.test(m[1]) && /^[A-Za-z0-9._-]+$/.test(m[2])) return `${m[1]}/${m[2]}`;
  }
  return null;
}

/** The entries of `git worktree list --porcelain`: { path, head, branch (null when detached), bare }. The first is the main one. */
export function parseWorktrees(porcelain) {
  const out = [];
  for (const chunk of (porcelain ?? "").split(/\r?\n\r?\n/)) {
    const entry = { path: null, head: null, branch: null, bare: false, detached: false };
    for (const line of chunk.split(/\r?\n/)) {
      if (line.startsWith("worktree ")) entry.path = line.slice(9);
      else if (line.startsWith("HEAD ")) entry.head = line.slice(5);
      else if (line.startsWith("branch ")) entry.branch = line.slice(7).replace(/^refs\/heads\//, "");
      else if (line === "bare") entry.bare = true;
      else if (line === "detached") entry.detached = true;
    }
    if (entry.path) out.push(entry);
  }
  return out;
}

/** Directories under each root (down to maxDepth) that hold a `.git` directory or file. Hidden and build folders are skipped. */
export function findRepoDirs(roots, maxDepth = MAX_DEPTH) {
  const found = new Set();
  const seen = new Set();
  const walk = (dir, depth) => {
    let real;
    try { real = fs.realpathSync(dir); } catch { return; }
    if (seen.has(real)) return;
    seen.add(real);
    let entries;
    try { entries = fs.readdirSync(real, { withFileTypes: true }); } catch { return; }
    if (entries.some((e) => e.name === ".git")) found.add(real);
    if (depth >= maxDepth) return;
    for (const e of entries) {
      if (!e.isDirectory() || e.name.startsWith(".") || SKIP.has(e.name)) continue;
      walk(path.join(real, e.name), depth + 1);
    }
  };
  for (const root of roots) walk(root, 0);
  return [...found];
}

function upstreamOf(worktreePath, branch) {
  if (!branch) return null;
  const remote = git(worktreePath, ["config", "--get", `branch.${branch}.remote`]);
  const merge = git(worktreePath, ["config", "--get", `branch.${branch}.merge`]);
  if (!remote || !merge) return null;
  return { remote, branch: merge.replace(/^refs\/heads\//, "") };
}

/**
 * Every checkout found from the roots, one row per worktree: { repo (owner/name or null), remote, kind
 * ("clone" for a repository's main working tree, "worktree" for one added with `git worktree add`), path, branch
 * (null when detached), detached, head, upstream ({ remote, branch } or null) }. Sorted by repo, then path.
 */
export function listCheckouts(roots) {
  const rows = [];
  const commonDirs = new Set();
  for (const dir of findRepoDirs(roots)) {
    const common = git(dir, ["rev-parse", "--path-format=absolute", "--git-common-dir"]);
    if (!common || commonDirs.has(common)) continue;
    commonDirs.add(common);
    const worktrees = parseWorktrees(git(dir, ["worktree", "list", "--porcelain"]));
    const remoteName = git(dir, ["remote"])?.split(/\r?\n/).filter(Boolean) ?? [];
    const remote = remoteName.includes("origin") ? "origin" : remoteName[0];
    const remoteUrl = remote ? git(dir, ["remote", "get-url", remote]) : null;
    const repo = parseRemote(remoteUrl);
    const head = remote ? git(dir, ["symbolic-ref", "--short", `refs/remotes/${remote}/HEAD`]) : null;
    const defaultBranch = head?.startsWith(`${remote}/`) ? head.slice(remote.length + 1) : null;
    worktrees.forEach((w, i) => {
      if (w.bare || !fs.existsSync(w.path)) return;
      rows.push({
        repo, remote: remote ?? null, defaultBranch, kind: i === 0 ? "clone" : "worktree", path: w.path,
        branch: w.branch, detached: !w.branch, head: w.head, upstream: upstreamOf(w.path, w.branch),
      });
    });
  }
  return rows.sort((a, b) => (a.repo ?? "~").localeCompare(b.repo ?? "~") || a.path.localeCompare(b.path));
}

/**
 * The branch names to look up as a pull request head for a row: its local branch, and its upstream branch when that
 * is another name. The base branch (the remote's default, else main or master) is never one: a worktree made with
 * `-b x origin/main` tracks main, and a clone on main has no pull request of its own. Empty when detached.
 */
export function headBranches(row) {
  if (!row.branch) return [];
  const base = row.defaultBranch ? [row.defaultBranch] : ["main", "master"];
  const heads = base.includes(row.branch) ? [] : [row.branch];
  const up = row.upstream?.branch;
  if (up && up !== row.branch && !base.includes(up)) heads.push(up);
  return heads;
}

/** A plain-text table of the rows. */
export function format(rows) {
  if (rows.length === 0) return "No git checkouts found.";
  const lines = rows.map((r) => {
    const branch = r.detached ? `(detached at ${String(r.head ?? "").slice(0, 7)})` : r.branch;
    const up = r.upstream ? ` -> ${r.upstream.remote}/${r.upstream.branch}` : r.detached ? "" : " (no upstream)";
    return `${r.repo ?? "(not a GitHub repo)"}  ${r.kind}  ${branch}${up}  ${r.path}`;
  });
  return ["repo  kind  branch  path", ...lines].join("\n");
}

function defaultRoots() {
  const cwd = process.cwd();
  return [...new Set([cwd, path.dirname(cwd), os.homedir(), "/home/user"])].filter((d) => {
    try { return fs.statSync(d).isDirectory(); } catch { return false; }
  });
}

if (process.argv[1] && fs.realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const json = args.includes("--json");
  const roots = args.filter((a) => a !== "--json");
  const rows = listCheckouts(roots.length > 0 ? roots : defaultRoots());
  console.log(json ? JSON.stringify(rows.map((r) => ({ ...r, headBranches: headBranches(r) })), null, 2) : format(rows));
}
