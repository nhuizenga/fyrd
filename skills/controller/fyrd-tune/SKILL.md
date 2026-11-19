---
name: fyrd-tune
description: "Fyrd Controller only: use when the Fyrd Controller is asked to start the Tuner, or to have it review a pull request or stack for ways to improve the Fyrd personas and skills. Never use this outside the Fyrd Controller session."
---

# fyrd-tune

Starts the Tuner (role `tuner`) and sends it manual runs. The Tuner persona holds the reviewing behavior: it sets up its own daily scheduled task (01:00 Pacific) and opens at most one issue per run in `nhuizenga/fyrd`. You only start it, give it links and tear it down. There is at most one live Tuner; never start a second one while one is live.

## Start the Tuner

Request: "Start the tuner", or a manual run (below) when no Tuner is live.

1. **Title.** `tuner-<suffix>`: the owner's suffix, else `tuner-1`. Find existing sessions with `mcp__claude-code-remote__list_sessions` (`mine: true`, `limit: 100`, page with `after_id`, ignore archived). If a live `tuner-…` session exists and this is a manual run, don't start one: send it the run (below). For "Start the tuner" with a live Tuner, say it is already running, unless the owner asked to restart it: then, if it is idle (`get_session` `status_bucket` other than `working` or `blocked` and its tags don't include `fyrd-workers-running`), mark it to recycle under the same title, but don't tear it down yet; if it is busy, ask with `AskUserQuestion` (`Restart anyway`, `Cancel`).
2. **Repo.** Always `nhuizenga/fyrd`, where the personas and skills live and where the Tuner opens its issues. Ask nothing, and never pick `No repo`.
3. **Session cap** as in `fyrd-peasant`, before deleting, archiving or creating anything (`AskUserQuestion`: `Start fewer now`, `Tear down idle sessions`, `Cancel`; `Tear down idle sessions` asks which with a `multiSelect` question; `Cancel` changes nothing).
4. **Recycle.** Only if the Tuner is actually being started: tear down the session marked in step 1 (delete its trigger, then archive, as below).
5. **Create.** Call `mcp__claude-code-remote__create_session` with `title`, `tags: ["fyrd-hub", "fyrd-role-tuner"]`, `environment_id` from `<hub-settings>` (omit when empty), `permission_mode: "auto"`, `model` from `<peasant-persona id="tuner" …>`, `append_system_prompt` (tuner persona text, blank line, `<shared-memory>…</shared-memory>`, then role memory in `<role-memory role="tuner">…</role-memory>` if any), and `source_url: "https://github.com/nhuizenga/fyrd"`. The `prompt` is `Set up your daily schedule.`, followed for a manual run by a new line `Then do a manual run: <link>` with the link verbatim.
6. Reply with the title and session id and stop. The Tuner reports its trigger id in its own session; you don't wait for it.

## Manual run

Request: "Tune <PR or stack url>" (or "Have the tuner look at <url>"). The link is a pull request, a stack, or any pull request in a stack, in any repo; pass it verbatim. A link to any pull request in a stack means the whole stack: the Tuner looks up the stack and reviews every pull request in it. There is no time limit on a manual run, and it isn't limited to pull requests Fyrd built.

1. Find the live Tuner as in Start step 1. If there is none, start it (Start above) with the manual run in its prompt.
2. Otherwise call `mcp__claude-code-remote__send_message` with its `session_id` and `Manual run: <link>`. A busy Tuner queues it; don't wait.
3. Reply with what you sent and to whom. The owner reads the outcome (the issue link, or why there is none) in the Tuner's session.

## Tear down the Tuner

Confirm with `AskUserQuestion` (`Tear down anyway`, `Cancel`) if it is busy (`status_bucket` `working` or `blocked`, or tagged `fyrd-workers-running`). Find its trigger with `mcp__claude-code-remote__list_triggers` (name equals the Tuner's title, or use the trigger id it reported) and call `mcp__claude-code-remote__delete_trigger` with `trigger_id`. Then call `mcp__claude-code-remote__archive_session` with the Tuner's `session_id`. Never rely on the Tuner to stop itself.
