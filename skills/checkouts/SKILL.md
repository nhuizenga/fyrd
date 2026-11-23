---
name: checkouts
description: "Checkouts: lists the git clones and worktrees in this session with their repo and branch, finds each branch's pull requests, and reports them to Fyrd in a fyrd-prs block. Use after you or a worker create, push or switch a branch, add a git worktree or open a pull request, or when asked what is checked out."
---
# Checkouts

Shows what this session has checked out and tells Fyrd which pull requests the session works on. Fyrd reads the `fyrd-prs` block from your reply and keeps each pull request with this session until the session is archived, so the console shows them even after a branch is gone or a PR is merged. Any persona can run it; it changes nothing.

1. **List the checkouts.** Run `node "${CLAUDE_SKILL_DIR}/checkouts.mjs" --json` (Node is the only requirement). It looks in the working directory, its parent, `$HOME` and `/home/user`, three levels down; pass other folders as arguments to look there instead. Each row is one checkout: `repo` (GitHub `owner/name`, or null), `kind` (`clone` for a repository's main working tree, `worktree` for one added with `git worktree add`), `branch` (null when detached), `upstream`, `path` and `headBranches`, the branch names to look up as a pull request head (never the base branch; empty when detached). If the script isn't there, find it with `find ~/.claude/plugins -path '*/checkouts/checkouts.mjs'`; if Node is missing, use `git worktree list --porcelain` and `git remote get-url origin` in each clone you know of.
2. **Show the list.** Give the reader one line per checkout: repo, kind, branch (or `detached at <sha>`) and path.
3. **Find the pull requests.** For each row with a `repo` and `headBranches`, look up the pull requests whose head is each branch, in every state (open, closed, merged), with the GitHub tools you have: the GitHub MCP `list_pull_requests` tool with `head: "<owner>:<branch>"` and `state: "all"`, or `GET /repos/{owner}/{repo}/pulls?head={owner}:{branch}&state=all`. Add pull requests you opened or were told you work on that aren't on a local branch. Skip a row your tools can't reach and say so; never guess a number.
4. **Report them.** If you found pull requests, end your own reply (not a worker's or subagent's: Fyrd reads only this session's replies) with one block whose info string is `fyrd-prs`, one URL per line and nothing else on the line:

   ````
   ```fyrd-prs
   https://github.com/<owner>/<repo>/pull/<number>
   ```
   ````

   Only the plain form `https://github.com/<owner>/<repo>/pull/<number>` counts: no trailing slash, `/files`, `#…`, bullet or Markdown link. Fyrd ignores any other line, takes at most 20 URLs from one block, and keeps at most 50 from blocks per session. If you found none, say so and write no block. Listing a PR again is harmless.

Checkouts with a detached HEAD, a branch with no pull request, or a repo that isn't on GitHub are listed in step 2 but never reported.
