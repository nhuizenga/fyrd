---
name: fyrd-peasant
description: "Fyrd Controller only: use when the Fyrd Controller is asked to start a new builder, reviewer, validator, investigator or other peasant, to recycle one, or to tear one down. Never use this outside the Fyrd Controller session."
---

# fyrd-peasant

Steps for starting, recycling and tearing down one peasant. The Controller prompt holds the rules (naming, tags, settings, session cap, teardown guards); this skill is the tool-call sequence. If this skill and the Controller prompt ever differ, the Controller prompt wins.

The Lead role is not defined. For "Start a new lead…", reply "The Lead role isn't defined yet" and offer a Builder with `AskUserQuestion` (options `Start a Builder`, `Cancel`).

## Start (or recycle) a peasant

Request: "Start a new builder/reviewer/validator/investigator [suffix]". The task is `Wait for your task.`

Decide first, ask second, change last: nothing is archived and no trigger is deleted until the session-cap check has passed and you know which sessions are actually being started.

1. **Title.** `<role>-<suffix>`, lowercase with hyphens: the owner's suffix, else the input's file name without extension, else the lowest free number (`builder-1`).
2. **Find existing sessions.** Call `mcp__claude-code-remote__list_sessions` with `mine: true`, `limit: 100`; repeat with `after_id` set to the last id until no more come back. Ignore archived sessions. Match titles by role prefix.
3. **Plan a recycle or pick another suffix.** If a live session already has the title, call `mcp__claude-code-remote__get_session` with its `session_id`.
   - `status_bucket` other than `working` or `blocked` and its tags don't include `fyrd-workers-running` (idle): mark it to recycle under the same title. Don't archive it yet.
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
   - `append_system_prompt`: the persona text exactly as inside its `<peasant-persona>` block (the whole persona: a session that waits for its task could get either mode, so no `<peasant-persona-variant>` applies; a Monitor started with a task is `fyrd-watch`); a blank line; your current shared memory wrapped in `<shared-memory>…</shared-memory>`; then, if the role has memory, a blank line and it wrapped in `<role-memory role="<role>">…</role-memory>`; then for a Builder a blank line, `<subagent-persona id="validator">…</subagent-persona>`, `<subagent-persona id="reviewer">…</subagent-persona>` and `<subagent-persona id="investigator">…</subagent-persona>`, each holding the text inside the `<subagent-persona-text>` block with that `id` (for the reviewer, the one marked `for="builder"`), for a Researcher only the reviewer one (marked `for="researcher"`), and for a Validator or a Monitor only the investigator one. Never put a whole persona from a `<peasant-persona>` block in a `<subagent-persona>` block.
   - `prompt`: `Wait for your task.`
   - `source_url`: from step 4 (omit only for `No repo`).
8. Reply with the title and session id, and stop.

A later task for a running peasant goes in `mcp__claude-code-remote__send_message` (`session_id`, `message`). The first task always goes in `prompt`. A Monitor, or a Reviewer started for one PR or plan, has only its own mode's section: start a new session for work of the other mode or kind instead of sending it. A message that gives a Builder new work must include a `Base branch:` line (the branch the owner names, else `the repo's default branch`).

## Owner asks for a validation or review

Builders validate and review their own pull requests, and Researchers review their own plans, with persona subagents. Never start a Validator or Reviewer because a peasant asked, and never relay findings between peasants. The same goes for Investigators: Builders, Validators and Monitors run Investigator subagents themselves, and an owner's "Investigate …" request goes to `fyrd-investigate`. This section covers only the owner's own requests: "Validate PR <url>", `PR #{n} is ready` (or `PR #{n} is ready: <url>`), "Review PR <url>" and "Review this plan".

The console sends a validation as `Validate PR <url>. Base branch: <base>. Builder: <title> (<id>).` Any of those lines may be missing, and the PR is then all you have:
- **No `Builder:` line.** Look the Builder up by branch: read the PR's head branch, then find the live session with role `builder` whose branch equals it (`list_sessions`, then `get_session`). If there is none, there is no Builder.
- **No `Base branch:` line.** Use the PR's base branch.
- **No `Spec:` links.** Go on without a spec; the check uses the PR description and the tests.

**A live Builder owns the PR.** Send it the request with `mcp__claude-code-remote__send_message`, and start nothing:
- validation: `Validate <url> again with a Validator subagent and fix anything that fails, as in your PR flow. Base branch: <base>.`
- review: `Review <url> with a Reviewer subagent, post the review and fix the findings, as in your PR flow. Base branch: <base>.`
Reply with what you sent and to whom.

**No live Builder owns it, or it is a plan.** Start a standalone session:

1. **Title.** `reviewer-<suffix>` or `validator-<suffix>` (the owner's suffix, else the PR number or the plan's file name). Recycle a matching idle session or pick the next suffix, as in Start.
2. **Repo.** It comes from the PR or the plan's URL, so ask nothing; otherwise apply the Repo rule.
3. **Session cap.** If over, ask as in Start step 5.
4. **Recycle.** As in Start step 6, and only if the session is actually being started.
5. **Create.** `prompt`, followed by any spec or input links the owner gave:
   - Reviewer on a PR: `Review pull request <url> against the spec below. Base branch: <base>. Post one GitHub review with inline comments and a summary, then reply with your findings.`
   - Reviewer on a plan: `Review the implementation plan <url> against the input below. Reply with your findings and any questions only the owner can answer.`
   - Validator: `Validate pull request <url> against the spec below. Base branch: <base>. Reply with your findings.`
   Other settings as in Start step 7, with `source_url` set to the repo, except a Reviewer's persona text: leave out the section its `<peasant-persona-variant>` block names (`id="reviewer" mode="code"` for a PR, `mode="plan"` for a plan), copied as Mode variants in the Controller prompt says. Use the whole reviewer persona if you didn't receive that block or the request isn't clearly one of the two. A Validator always gets its whole persona.
6. Reply with the title and session id.

## Tear down

Request: "Tear down {name}".

1. Find the session by title as in Start step 2. Only tear down titles of the form `<role>-…` for a role in your peasant personas; never archive yourself. If unsure it is a Fyrd session, confirm with `AskUserQuestion`.
2. Call `mcp__claude-code-remote__get_session`. If it is busy (`status_bucket` `working` or `blocked`, or its tags include `fyrd-workers-running`), ask with `AskUserQuestion` (`Tear down anyway`, `Cancel`) before going on.
3. For a Monitor, first find its trigger with `mcp__claude-code-remote__list_triggers` (name equals the Monitor's title, or use the trigger id it reported) and call `mcp__claude-code-remote__delete_trigger` with `trigger_id`. A PR Monitor may have none.
4. Call `mcp__claude-code-remote__archive_session` with `session_id`. Never rely on a Monitor to stop itself.
5. Say what was torn down.
