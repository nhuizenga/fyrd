---
name: research
description: "Fyrd Controller only: use when the Fyrd Controller is asked to research one or more GitHub file URLs or claude.ai artifact links and help build an implementation plan. Never use this outside the Fyrd Controller session."
kind: controller-skill
---

# research

Request: "Research {uri} and help me build an implementation plan". Start one Researcher per uri, all at once. You do no research yourself. The naming, tag, recycle and session-cap rules are the Controller prompt's; `fyrd:peasant` has the full step list.

1. **Titles.** For each uri: `researcher-<suffix>` (the owner's suffix, else the uri's file name without extension, else `researcher-1`, `researcher-2`, …). Find existing sessions with `mcp__claude-code-remote__list_sessions` (`mine: true`, `limit: 100`, page with `after_id`, ignore archived). A live idle session with the same title (`get_session` `status_bucket` other than `working` or `blocked` and its tags don't include `fyrd-workers-running`): mark it to recycle under the same title, but don't archive it yet. Busy: next suffix.
2. **Repo.** For each uri apply the Repo rule in the Controller prompt: infer the repo, else ask with `AskUserQuestion` (likely repos plus `No repo`), and decide it before the session-cap check.
3. **Session cap.** Count once for the whole request: live Fyrd sessions (you plus every live peasant) plus the new Researchers, against `session_cap`. If over, ask with `AskUserQuestion` (`Start fewer now`, `Tear down idle sessions`, `Cancel`) before archiving or creating anything, and act as `fyrd:peasant` describes (`Tear down idle sessions` asks which with a `multiSelect` question; `Cancel` archives nothing).
4. **Recycle.** Only for the Researchers actually being started, archive each session marked in step 1 with `mcp__claude-code-remote__archive_session`.
5. **Create.** Call `mcp__claude-code-remote__create_session` once per uri, in parallel, with:
   - `title`: from step 1.
   - `tags`: `["fyrd-hub", "fyrd-role-researcher"]`.
   - `environment_id` from `<hub-settings>` (omit when empty), `permission_mode: "auto"`, `model` from `<peasant-persona id="researcher" …>`.
   - `append_system_prompt`: the researcher persona text, a blank line, `<shared-memory>…</shared-memory>`, then if the role has memory a blank line and `<role-memory role="researcher">…</role-memory>`, then a blank line and `<subagent-persona id="reviewer">…</subagent-persona>` (the text inside `<subagent-persona-text id="reviewer" for="researcher">`, not the whole reviewer persona).
   - `prompt`: `Research the input below and help the owner build an implementation plan. Investigate it in depth and ask the owner with AskUserQuestion whenever something is unclear or a decision is needed, until each work item is detailed enough for a Builder to build without guessing. Have a Reviewer subagent review the plan and make its suggested changes. Then commit the plan as markdown to your branch and push it, publish it as a claude.ai artifact, then reply with the artifact link, branch, file path and a short summary.` followed by the uri verbatim on its own line.
   - `source_url` from step 2 (omit only for `No repo`).
6. Reply listing each Researcher's title and session id, and stop. The owner reads results in the Researchers' own sessions.
