---
name: fyrd-peasant
description: "Fyrd Controller only: use when the Fyrd Controller is asked to start a new builder, reviewer, validator or other peasant, to recycle one, or to tear one down. Never use this outside the Fyrd Controller session."
---

# fyrd-peasant

Steps for starting, recycling and tearing down one peasant. The Controller prompt holds the rules (naming, tags, settings, session cap, teardown guards); this skill is the tool-call sequence. If this skill and the Controller prompt ever differ, the Controller prompt wins.

The Lead role is not defined. For "Start a new lead…", reply "The Lead role isn't defined yet" and offer a Builder with `AskUserQuestion` (options `Start a Builder`, `Cancel`).

## Start (or recycle) a peasant

Request: "Start a new builder/reviewer/validator [suffix]". The task is `Wait for your task.`

Decide first, ask second, change last: nothing is archived and no trigger is deleted until the session-cap check has passed and you know which sessions are actually being started.

1. **Title.** `<role>-<suffix>`, lowercase with hyphens: the owner's suffix, else the input's file name without extension, else the lowest free number (`builder-1`).
2. **Find existing sessions.** Call `mcp__claude-code-remote__list_sessions` with `mine: true`, `limit: 100`; repeat with `after_id` set to the last id until no more come back. Ignore archived sessions. Match titles by role prefix.
3. **Plan a recycle or pick another suffix.** If a live session already has the title, call `mcp__claude-code-remote__get_session` with its `session_id`.
   - `status_bucket` other than `working` or `blocked` (idle): mark it to recycle under the same title. Don't archive it yet.
   - Busy: try the next suffix (`-2`, `-3`, …) with the same check.
   Archive-and-recreate is the only reset; slash commands like `/clear` can't be sent.
4. **Repo.** Apply the Repo rule in the Controller prompt: infer the repo, else ask with `AskUserQuestion` (likely repos plus `No repo`). This includes `Wait for your task.` starts: "Start a new builder" asks which repo. Decide it before the session-cap check.
5. **Session cap.** Count live Fyrd sessions: you plus every live peasant (recycling doesn't change the count). If the new session would exceed `session_cap` from `<hub-settings>`, call `AskUserQuestion` with the options `Start fewer now`, `Tear down idle sessions`, `Cancel`:
   - `Start fewer now`: start only as many as fit, and say which were left out.
   - `Tear down idle sessions`: ask again with a `multiSelect: true` `AskUserQuestion` whose options are the idle peasants by title, each Monitor labeled as watching (for example `monitor-support (watching #support)`). Tear down only the ones the owner picks (Tear down below), then start what now fits. Never tear down a session the owner didn't pick.
   - `Cancel`: start none, and archive nothing.
6. **Recycle.** Only now, and only for sessions actually being started, tear down each session marked in step 3 (Tear down below; for a Monitor, delete its trigger first).
7. **Create.** Call `mcp__claude-code-remote__create_session` with:
   - `title`: from step 1.
   - `tags`: exactly `["fyrd-hub", "fyrd-role-<role>"]`.
   - `environment_id`: from `<hub-settings>` (omit when empty).
   - `permission_mode`: `"auto"`.
   - `model`: the `model` attribute of that role's `<peasant-persona>`.
   - `append_system_prompt`: the persona text exactly as inside its `<peasant-persona>` block; a blank line; your current shared memory wrapped in `<shared-memory>…</shared-memory>`; then, if the role has memory, a blank line and it wrapped in `<role-memory role="<role>">…</role-memory>`.
   - `prompt`: `Wait for your task.`
   - `source_url`: from step 4 (omit only for `No repo`).
8. Reply with the title and session id, and stop.

A later task for a running peasant goes in `mcp__claude-code-remote__send_message` (`session_id`, `message`). The first task always goes in `prompt`. A message that gives a Builder new work must include a `Base branch:` line (the branch the owner names, else `the repo's default branch`).

## Review request from a Builder

A message starting `Review request:` from a Builder names a PR, its base branch, the spec links and the Builder's title and session id. Start a Reviewer and a Validator for it.

1. **Titles.** `reviewer-<builder suffix>` and `validator-<builder suffix>`. Recycle a matching idle session or pick the next suffix, as in Start.
2. **Repo.** It comes from the PR, so ask nothing.
3. **Session cap.** Check once for both sessions. If over, ask as in Start step 5.
4. **Recycle.** Only for sessions actually being started.
5. **Create.** Reviewer `prompt`: `Review pull request <url> against the spec below. Post one GitHub review with inline comments and a summary, then reply with your findings.` followed by the spec links. Validator `prompt`: `Validate pull request <url> against the spec below. Reply with your findings. If anything fails, also send them to the Builder with send_message (session <builder id>).` followed by the spec links. Other settings as in Start step 7, with `source_url` set to the PR's repo.
6. Reply with both titles and session ids.

## Tear down

Request: "Tear down {name}".

1. Find the session by title as in Start step 2. Only tear down titles of the form `<role>-…` for a role in your peasant personas; never archive yourself. If unsure it is a Fyrd session, confirm with `AskUserQuestion`.
2. Call `mcp__claude-code-remote__get_session`. If it is busy (`status_bucket` `working` or `blocked`), ask with `AskUserQuestion` (`Tear down anyway`, `Cancel`) before going on.
3. For a Monitor, first find its trigger with `mcp__claude-code-remote__list_triggers` (name equals the Monitor's title, or use the trigger id it reported) and call `mcp__claude-code-remote__delete_trigger` with `trigger_id`. A PR Monitor may have none.
4. Call `mcp__claude-code-remote__archive_session` with `session_id`. Never rely on a Monitor to stop itself.
5. Say what was torn down.
