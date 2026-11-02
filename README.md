# fyrd

Build software with an army of specialized agents, managed from a one-click console.

## Usage

Requires Node, the .NET 10 SDK, and npm.

- `/fyrd:up` builds (first run only) and starts the console on `127.0.0.1` (port 4870, or the next free one), then opens it in your browser. Running it again reuses the running server.
- `/fyrd:status` shows the URL, spec count, and decisions waiting.
- `/fyrd:down` stops the server.
- In the console's Sessions list, the download icon on a session's row exports all of its saved events as raw JSONL (a header line, one event per line, an `fyrd_export_end` trailer; a file without the trailer is incomplete). The same file is at `GET /api/hub/sessions/{id}/export`. The export holds everything the session saw, including tool output and any secrets it printed, and Fyrd does no redaction.

- In the console's session details, a pull request that is part of a stack (an open PR whose base is another open PR's head) shows the chain, and its menu offers **Merge stack**. That squash-merges the whole chain top down to the root, waiting for each PR's checks before merging it (polling every 20 s, at most 30 minutes per PR), and stops with the reason on a failure, a conflict, a PR closed or merged by someone else, an unexpected base change or a timeout. Cancel stops before the next merge and never undoes merges already done. Stack-merge jobs live in the server's memory only: a restart drops a running job, leaving some PRs merged and the rest open. Click Merge stack again and the chain is recomputed from what is still open, so it resumes safely.

Runtime state (PID/port, log) lives in `~/.fyrd/run/`; data lives in `~/.fyrd/data/`.

Environment variables:

- `FYRD_HOME` moves the data directory (default `~/.fyrd/data`). It moves data only; `~/.fyrd/run` (PID/port, log) is always shared.
- `FYRD_NO_BROWSER=1` stops `/fyrd:up` from opening a browser.
- `GH_TOKEN` (or, as a fallback, `GITHUB_TOKEN`) is the GitHub token Fyrd uses to read pull requests, mark them ready and squash-merge them. Without it no session shows any PRs, and `/fyrd:up` prints a warning. A classic token with `repo` works; a fine-grained token needs **Pull requests: read/write**, **Contents: read/write**, **Commit statuses: read** and **Checks: read** on the repos Fyrd works in. The server reads it once at startup and keeps it in memory only, so after changing it run `/fyrd:down` then `/fyrd:up`. If you use `gh`, `export GH_TOKEN=$(gh auth token)` before `/fyrd:up`. `/fyrd:status` shows `GitHub: signed in as <login>`.

## Tests

```
dotnet test runtime/Fyrd.sln
cd console && npm test
node --test "scripts/*.test.mjs"
```

## Local install

```
/plugin marketplace add C:/Users/nhuizenga/repos/personal/fyrd
/plugin install fyrd@fyrd
```

After edits, run `/reload-plugins`.
