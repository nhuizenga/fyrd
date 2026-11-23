import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { parseRemote, parseWorktrees, findRepoDirs, listCheckouts, headBranches, format } from "../skills/checkouts/checkouts.mjs";

const script = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "skills", "checkouts", "checkouts.mjs");
const tmp = () => fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), "fyrd-checkouts-")));
const git = (cwd, ...args) => execFileSync("git", args, { cwd, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }).trim();

function repo(dir, remote) {
  fs.mkdirSync(dir, { recursive: true });
  git(dir, "init", "-q", "-b", "main");
  git(dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "init");
  if (remote) git(dir, "remote", "add", "origin", remote);
  return dir;
}

test("parseRemote reads GitHub https, ssh and the cloud git proxy, and nothing else", () => {
  assert.equal(parseRemote("https://github.com/acme/widgets"), "acme/widgets");
  assert.equal(parseRemote("https://github.com/acme/widgets.git"), "acme/widgets");
  assert.equal(parseRemote("https://x-access-token:abc@github.com/acme/widgets.git"), "acme/widgets");
  assert.equal(parseRemote("git@github.com:acme/my.repo.git"), "acme/my.repo");
  assert.equal(parseRemote("ssh://git@github.com/acme/widgets"), "acme/widgets");
  assert.equal(parseRemote("http://local_proxy@127.0.0.1:37061/git/acme/widgets"), "acme/widgets");
  assert.equal(parseRemote("https://gitlab.com/acme/widgets"), null);
  assert.equal(parseRemote("https://github.com.evil.example/acme/widgets"), null);
  assert.equal(parseRemote("https://github.com/acme/widgets/tree/main"), null);
  assert.equal(parseRemote(""), null);
  assert.equal(parseRemote(null), null);
});

test("parseWorktrees reads branches, detached heads and bare entries", () => {
  const rows = parseWorktrees("worktree /a\nHEAD 111\nbranch refs/heads/main\n\nworktree /b\nHEAD 222\ndetached\n\nworktree /c\nbare\n");
  assert.deepEqual(rows.map((r) => [r.path, r.branch, r.detached, r.bare]), [["/a", "main", false, false], ["/b", null, true, false], ["/c", null, false, true]]);
  assert.deepEqual(parseWorktrees(""), []);
});

test("listCheckouts finds a clone and its worktrees, with branch, upstream and detached state", () => {
  const root = tmp();
  const main = repo(path.join(root, "widgets"), "https://github.com/acme/widgets.git");
  git(main, "worktree", "add", "-q", "-b", "fyrd/feature", path.join(root, "widgets-feature"));
  git(main, "worktree", "add", "-q", "--detach", path.join(root, "widgets-detached"));
  git(main, "config", "branch.fyrd/feature.remote", "origin");
  git(main, "config", "branch.fyrd/feature.merge", "refs/heads/fyrd/remote-name");
  repo(path.join(root, "nested", "plain"), null);

  const rows = listCheckouts([root]);
  const byPath = Object.fromEntries(rows.map((r) => [path.basename(r.path), r]));
  assert.equal(rows.length, 4);
  assert.deepEqual(
    [byPath["widgets"].kind, byPath["widgets"].repo, byPath["widgets"].branch, byPath["widgets"].upstream],
    ["clone", "acme/widgets", "main", null]);
  assert.equal(byPath["widgets-feature"].kind, "worktree");
  assert.deepEqual(byPath["widgets-feature"].upstream, { remote: "origin", branch: "fyrd/remote-name" });
  assert.deepEqual(headBranches(byPath["widgets-feature"]), ["fyrd/feature", "fyrd/remote-name"]);
  assert.equal(byPath["widgets-detached"].detached, true);
  assert.deepEqual(headBranches(byPath["widgets-detached"]), []);
  assert.equal(byPath["plain"].repo, null);
  assert.deepEqual(headBranches(byPath["widgets"]), []); // the base branch is no PR head
  // A branch that tracks the base branch is looked up by its own name only.
  assert.deepEqual(headBranches({ branch: "fyrd/x", upstream: { remote: "origin", branch: "main" }, defaultBranch: null }), ["fyrd/x"]);
  assert.deepEqual(headBranches({ branch: "fyrd/x", upstream: { remote: "origin", branch: "develop" }, defaultBranch: "develop" }), ["fyrd/x"]);

  // Starting from a worktree finds the same repository once.
  assert.equal(listCheckouts([path.join(root, "widgets-feature"), main]).length, 3);
  const text = format(rows);
  assert.match(text, /acme\/widgets {2}worktree {2}fyrd\/feature -> origin\/fyrd\/remote-name/);
  assert.match(text, /\(detached at [0-9a-f]{7}\)/);
  assert.match(text, /\(not a GitHub repo\)/);
});

test("findRepoDirs skips hidden and dependency folders and stops at the depth limit", () => {
  const root = tmp();
  repo(path.join(root, ".cache", "hidden"), null);
  repo(path.join(root, "node_modules", "dep"), null);
  repo(path.join(root, "a", "b", "c", "d"), null);
  repo(path.join(root, "a", "ok"), null);
  assert.deepEqual(findRepoDirs([root]).map((d) => path.relative(root, d)), [path.join("a", "ok")]);
});

test("the script prints a table, or JSON with the head branch, and finds nothing in an empty folder", () => {
  const root = tmp();
  repo(path.join(root, "widgets"), "git@github.com:acme/widgets.git");
  const json = JSON.parse(execFileSync(process.execPath, [script, "--json", root], { encoding: "utf8" }));
  assert.deepEqual(json.map((r) => [r.repo, r.kind, r.branch, r.headBranches]), [["acme/widgets", "clone", "main", []]]);
  assert.match(execFileSync(process.execPath, [script, root], { encoding: "utf8" }), /^repo {2}kind {2}branch {2}path\nacme\/widgets {2}clone {2}main \(no upstream\)/);
  assert.equal(execFileSync(process.execPath, [script, tmp()], { encoding: "utf8" }).trim(), "No git checkouts found.");
});
