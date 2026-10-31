---
name: fyrd-research
description: "Fyrd Controller only: use when the Fyrd Controller is asked to research one or more GitHub file URLs or claude.ai artifact links and help build an implementation plan. Never use this outside the Fyrd Controller session."
---

# fyrd-research

Request: "Research {uri} and help me build an implementation plan". Start one Researcher per uri, all at once. You do no research yourself. The naming, tag, recycle and session-cap rules are the Controller prompt's; `fyrd-peasant` has the full step list.

1. **Titles.** For each uri: `researcher-<suffix>` (the owner's suffix, else the uri's file name without extension, else `researcher-1`, `researcher-2`, …). Find existing sessions with `mcp__claude-code-remote__list_sessions` (`mine: true`, `limit: 100`, page with `after_id`, ignore archived). A live idle session with the same title (`get_session` `status_bucket` other than `working` or `blocked`): archive it with `mcp__claude-code-remote__archive_session`, then recreate under the same title. Busy: next suffix.
2. **Session cap.** Count once for the whole request: live Fyrd sessions (you plus every live peasant) plus the new Researchers, against `session_cap`. If over, ask with `AskUserQuestion` (`Start fewer now`, `Tear down idle sessions`, `Cancel`) before creating any, and act as `fyrd-peasant` describes.
3. **Create.** Call `mcp__claude-code-remote__create_session` once per uri, in parallel, with:
   - `title`: from step 1.
   - `tags`: `["fyrd-hub", "fyrd-role-researcher"]`.
   - `environment_id` from `<hub-settings>` (omit when empty), `permission_mode: "auto"`, `model` from `<peasant-persona id="researcher" …>`.
   - `append_system_prompt`: the researcher persona text, a blank line, `<shared-memory>…</shared-memory>`, then if the role has memory a blank line and `<role-memory role="researcher">…</role-memory>`.
   - `prompt`: `Research the input below and help the owner build an implementation plan. Investigate it, then write your findings and a proposed plan in your reply. Ask the owner with AskUserQuestion where a decision is needed.` followed by the uri verbatim on its own line.
   - `source_url` only if the request names the one repo to work in.
4. Reply listing each Researcher's title and session id, and stop. The owner reads results in the Researchers' own sessions.
