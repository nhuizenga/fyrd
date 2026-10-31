---
name: fyrd-watch
description: "Fyrd Controller only: use when the Fyrd Controller is asked to watch a pull request and fix what comes up, or to start a watcher on a Slack channel. Never use this outside the Fyrd Controller session."
---

# fyrd-watch

Starts a Monitor (role `monitor`). The Monitor persona holds the watching behavior; you only start it and give it the task. The Slack Monitor only writes suggestions in its own session and never posts to Slack. Never ask it to.

## Start a Monitor

1. **Title.** `monitor-<suffix>`: the owner's suffix, else `pr-<number>` for a PR (`monitor-pr-42`) or the channel name for Slack (`monitor-support`), else `monitor-1`. Find existing sessions with `mcp__claude-code-remote__list_sessions` (`mine: true`, `limit: 100`, page with `after_id`, ignore archived). A live idle session with the same title: tear it down first (delete its trigger, then archive, as below), then recreate under the same title. Busy: next suffix.
2. **Session cap** as in `fyrd-peasant` (`AskUserQuestion`: `Start fewer now`, `Tear down idle sessions`, `Cancel`).
3. **Create.** Call `mcp__claude-code-remote__create_session` with `title`, `tags: ["fyrd-hub", "fyrd-role-monitor"]`, `environment_id` from `<hub-settings>` (omit when empty), `permission_mode: "auto"`, `model` from `<peasant-persona id="monitor" …>`, and `append_system_prompt` (monitor persona text, blank line, `<shared-memory>…</shared-memory>`, then role memory in `<role-memory role="monitor">…</role-memory>` if any). The `prompt` depends on the mode:
   - **PR mode** ("Watch #{pr} and fix anything that comes up"): `PR mode. Watch pull request <owner>/<repo>#<number> (<url>) and fix anything that comes up.` If the repo is not clear from the request, ask with `AskUserQuestion`.
   - **Slack mode** ("Start a new watcher on {slack channel}…"): `Slack mode. Watch #<channel> and suggest answers and fixes for new messages. Owner's instructions: <the owner's instructions>.` Never include any instruction to post, reply, react or edit in Slack.
   Add `source_url` only if the task names the one repo to work in.
4. Reply with the title and session id and stop. A Slack Monitor reports its trigger id in its own session; you don't wait for it.

## Tear down a Monitor

Confirm with `AskUserQuestion` (`Tear down anyway`, `Cancel`) if it is busy. Find its trigger with `mcp__claude-code-remote__list_triggers` (name equals the Monitor's title, or use the trigger id it reported) and call `mcp__claude-code-remote__delete_trigger` with `trigger_id`. Then call `mcp__claude-code-remote__archive_session` with the Monitor's `session_id`. A PR Monitor may have no trigger. Never rely on a Monitor to stop itself.
