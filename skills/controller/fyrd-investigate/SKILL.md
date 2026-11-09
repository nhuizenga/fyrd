---
name: fyrd-investigate
description: "Fyrd Controller only: use when the Fyrd Controller is asked to investigate a failing GitHub workflow run, an incident, a suspected bug or an ad hoc finding and recommend a response. Never use this outside the Fyrd Controller session."
---

# fyrd-investigate

Request: "Investigate {input}", where each input is a failing workflow run, an incident (for example an `#oc-incidents` Slack thread), an issue or suspected bug, or a plain description of a finding. Start one Investigator per input, all at once. You investigate nothing yourself, and you never read the incident thread or the logs. The naming, tag, recycle and session-cap rules are the Controller prompt's; `fyrd-peasant` has the full step list.

Never start an Investigator because a peasant asked: Builders, Validators and Monitors run Investigator subagents in their own sessions. This skill is for the owner's own requests.

1. **Titles.** For each input: `investigator-<suffix>`: the owner's suffix, else `run-<run id>` for a workflow run, `pr-<number>` for a PR, `issue-<number>` for an issue, the channel name for a Slack thread (`investigator-oc-incidents`), the file name without extension for a file, else `investigator-1`, `investigator-2`, …. Find existing sessions with `mcp__claude-code-remote__list_sessions` (`mine: true`, `limit: 100`, page with `after_id`, ignore archived). A live idle session with the same title (`get_session` `status_bucket` other than `working` or `blocked` and its tags don't include `fyrd-workers-running`): mark it to recycle under the same title, but don't archive it yet. Busy: next suffix.
2. **Repo.** For each input apply the Repo rule in the Controller prompt: a workflow run, PR, issue or file URL gives its repo; otherwise ask with `AskUserQuestion` (likely repos plus `No repo`). An incident often spans systems, so `No repo` is a fair answer; the Investigator attaches the repos it needs itself. Decide it before the session-cap check.
3. **Session cap.** Count once for the whole request: live Fyrd sessions (you plus every live peasant) plus the new Investigators, against `session_cap`. If over, ask with `AskUserQuestion` (`Start fewer now`, `Tear down idle sessions`, `Cancel`) before archiving or creating anything, and act as `fyrd-peasant` describes (`Tear down idle sessions` asks which with a `multiSelect` question; `Cancel` archives nothing).
4. **Recycle.** Only for the Investigators actually being started, archive each session marked in step 1 with `mcp__claude-code-remote__archive_session`.
5. **Create.** Call `mcp__claude-code-remote__create_session` once per input, in parallel, with:
   - `title`: from step 1.
   - `tags`: `["fyrd-hub", "fyrd-role-investigator"]`.
   - `environment_id` from `<hub-settings>` (omit when empty), `permission_mode: "auto"`, `model` from `<peasant-persona id="investigator" …>`.
   - `append_system_prompt`: the investigator persona text, a blank line, `<shared-memory>…</shared-memory>`, then if the role has memory a blank line and `<role-memory role="investigator">…</role-memory>`.
   - `prompt`: `Investigate the problem below. Find the root cause and recommend a response (fix, mitigate, retry, escalate or no action). Ask the owner with AskUserQuestion whenever something is unclear. Change nothing outside a scratch worktree and post nothing anywhere. Publish your findings as a claude.ai artifact, then reply with the artifact link, the root cause and its confidence, and the recommended response.` followed by the input verbatim on its own line (a link, or the owner's description quoted as they wrote it).
   - `source_url` from step 2 (omit only for `No repo`).
6. Reply listing each Investigator's title and session id, and stop. The owner reads results in the Investigators' own sessions. If the owner then wants the fix built, that is a new "Build the following: <artifact link>" request (`fyrd-build`).
