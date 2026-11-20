# fyrd

Build software with an army of specialized agents, managed from a one-click console.

Fyrd is a Claude Code plugin. `/fyrd:up` starts a local console (a .NET service and a web UI) that runs a **Controller**, a Claude Code cloud session that takes your requests and starts **peasants**: cloud sessions that each play one role.

| Role | Does |
| --- | --- |
| Builder | Builds one piece of work and sees its pull requests through validation and review |
| Researcher | Researches a request and builds an implementation plan with you |
| Validator | Checks that a branch or pull request does what its spec says |
| Reviewer | Reviews a branch, pull request or implementation plan |
| Investigator | Looks into a failing run, an incident or a suspected bug and recommends what to do |
| Monitor | Watches one pull request or Slack channel |
| Tuner | Reviews finished Fyrd work and proposes changes to the Fyrd personas and skills, in a private claude.ai artifact |

The repository is public and MIT licensed.

## Install

Requires:

- Claude Code, Node, npm and the .NET 10 SDK.
- The `claude` CLI on your `PATH`, signed in with a claude.ai account that can use Claude Code on the web: run `claude auth login`. Fyrd creates the Controller and sends it messages through that CLI, and reads its claude.ai OAuth login from `~/.claude/.credentials.json` to follow the cloud sessions.
- A GitHub token for anything with pull requests, which is most work: see [Configuration](#configuration).

Windows is the primary platform. On macOS and Linux, Fyrd gives `claude` a terminal with the `script` utility (util-linux or BSD), so that must be installed, and **Settings › Environment** can't save secrets: set the environment variables instead.

Fyrd reads the login only from `~/.claude/.credentials.json`, with no other fallback. Claude Code on macOS usually keeps it in the Keychain and may not write that file, so following and creating cloud sessions on macOS is untested and may fail with "Signed out of Claude" and "Run `claude auth login`".

```
/plugin marketplace add nhuizenga/fyrd
/plugin install fyrd@fyrd
```

To work from a local clone instead, add the clone's folder as the marketplace (`/plugin marketplace add /path/to/fyrd`), and run `/reload-plugins` after edits.

To update, run `/plugin marketplace update fyrd`, then `claude plugin update fyrd@fyrd` in a terminal (the marketplace update alone leaves the installed plugin at its old version), then `/reload-plugins` (or restart Claude Code), `/fyrd:down` and `/fyrd:up`.

## Usage

- `/fyrd:up` builds (first run only) and starts the console on `127.0.0.1` (port 4870, or the next free one), then opens it in your browser. Running it again reuses the running server.
- `/fyrd:status` shows the URL, the session count, the questions waiting, the GitHub sign-in and how sessions get their session skills (`inline`, or `installed` from the fyrd plugin).
- `/fyrd:down` stops the server.

In the console, open Sessions and choose **Create Controller** to start the Controller; [docs/controller-skills.md](docs/controller-skills.md) covers how it is created and how it gets its instructions.

### Skills

All of Fyrd's prompts are skills of this plugin, at `skills/<name>/SKILL.md`, loaded as `fyrd:<name>`.

- **Request skills** (`kind: controller-skill`), used by the Controller only, one per request type: `/fyrd:build`, `/fyrd:research`, `/fyrd:investigate`, `/fyrd:watch`, `/fyrd:tune` and `/fyrd:peasant` (start, recycle or tear down a peasant). See [docs/controller-skills.md](docs/controller-skills.md).
- **Persona skills** (`kind: persona`), the full instructions of each role: `/fyrd:builder`, `/fyrd:controller`, `/fyrd:investigator`, `/fyrd:monitor`, `/fyrd:researcher`, `/fyrd:reviewer`, `/fyrd:tuner`, `/fyrd:validator`. Fyrd gives each session its persona in its system prompt; no session loads one by itself, but you can run one in any session to make it act in that role, or to read it; `/fyrd:controller` is only for reading, since Fyrd sends the Controller persona to the Controller session itself.
- **Session skills**, parts of a role's instructions that only some tasks need: `fyrd:stacked-pull-requests`, which a Builder loads when its work is split into more than one pull request, `fyrd:validate-dbt`, which a Builder, its workers and a Validator load for a change to a dbt project, and `fyrd:review-data-modeling`, which a Builder loads and gives a second Reviewer for a dbt or warehouse diff. You edit them under **Settings › Session skills**.

### Cloud environment setup

By default every session gets its session skills and the Controller its request skills as text in their instructions. Paste the short setup script the console shows (**Settings › Session skills**) into your cloud environment and it installs the fyrd plugin from this repository, so sessions load the skills on demand and start with shorter instructions. Once the script is pasted and the environment saved, click **I've pasted this script** in **Settings › Session skills**: until you do, sessions keep getting the skills as text. Then replace the Controller (Sessions › **Replace Controller**, then **Create Controller**), since a Controller that started before the plugin was installed doesn't have its skills. [docs/session-skills.md](docs/session-skills.md) is the step-by-step guide.

### Personas

**Settings › Personas** is a table of the personas where you change only each persona's model; **Reset** goes back to the default in the persona's front matter. A persona's name and text always come from `skills/<id>/SKILL.md`. Fyrd reads persona text once when it starts, so after a `git pull` that changes a persona, run `/fyrd:down` then `/fyrd:up`. Request skills are re-read on every poll, so in inline mode a pull is enough for them. In plugin mode (the plugin installed and confirmed in Settings › Session skills), a pull changes only the names and descriptions the Controller sees: a change to a request skill's body reaches the Controller only after the plugin's version is bumped, the cloud environment's setup script runs again (it runs `claude plugin marketplace update fyrd` and `claude plugin update fyrd@fyrd` when the environment snapshot is refreshed) and a new Controller session starts (see `docs/controller-skills.md`).

### Sessions and pull requests

- In the Sessions list, the download icon on a session's row exports all of its saved events as raw JSONL (a header line, one event per line, an `fyrd_export_end` trailer; a file without the trailer is incomplete). The same file is at `GET /api/hub/sessions/{id}/export`. The export holds everything the session saw, including tool output and any secrets it printed, and Fyrd does no redaction.
- In a session's details, a pull request that is part of a stack (an open PR whose base is another open PR's head) shows the chain, and its menu offers **Merge stack**. That squash-merges the whole chain top down to the root, waiting for each PR's checks before merging it (polling every 20 s, at most 30 minutes per PR; after a merge the next PR must show its new head commit, and "no checks yet" counts as not yet for 2 minutes; each merge is pinned to the head commit that was checked, so a push in between makes GitHub refuse it; a GitHub rate limit is waited out), and stops with the reason on a failure, a conflict, a PR closed or merged by someone else, an unexpected base change or a timeout. Cancel stops before the next merge and never undoes merges already done. Stack-merge jobs live in the server's memory only: a restart drops a running job, leaving some PRs merged and the rest open. Click Merge stack again and the chain is recomputed from what is still open, so it resumes safely.

## Configuration

Runtime state (PID/port, log) lives in `~/.fyrd/run/`; data lives in `~/.fyrd/data/`.

Environment variables:

- `FYRD_HOME` moves the data directory (default `~/.fyrd/data`). It moves data only; `~/.fyrd/run` (PID/port, log) is always shared.
- `FYRD_NO_BROWSER=1` stops `/fyrd:up` from opening a browser.
- `GH_TOKEN` (or, as a fallback, `GITHUB_TOKEN`) is the GitHub token Fyrd uses to read pull requests, mark them ready and squash-merge them. Without it no session shows any PRs, and `/fyrd:up` prints a warning. A classic token with `repo` works; a fine-grained token needs **Pull requests: read/write**, **Contents: read/write**, **Commit statuses: read** and **Checks: read** on the repos Fyrd works in. The server reads it at startup and keeps it in memory only. On Windows you can also save a token (and an optional Anthropic API key) under **Settings › Environment**: it is stored encrypted with Windows DPAPI, overrides `GH_TOKEN`, applies immediately, and "Remove saved value" goes back to the environment variable. On macOS and Linux saving is not available (Save is disabled), so use the environment variable. After changing the environment variable, run `/fyrd:down` then `/fyrd:up`. If you use `gh`, `export GH_TOKEN=$(gh auth token)` before `/fyrd:up`. `/fyrd:status` shows `GitHub: signed in as <login>`.

## Tests

```
dotnet test runtime/Fyrd.sln
cd console && npm test
node --test "scripts/*.test.mjs"
```

## License

[MIT](LICENSE)
