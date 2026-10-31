---
name: fyrd-build
description: "Fyrd Controller only: use when the Fyrd Controller is asked to build from a list of GitHub file URLs or claude.ai artifact links, such as plans or specs. Never use this outside the Fyrd Controller session."
---

# fyrd-build

Request: "Build the following: {uri list}". Start one Builder per uri, all at once. You write no code yourself. The naming, tag, recycle and session-cap rules are the Controller prompt's; `fyrd-peasant` has the full step list.

1. **Titles.** For each uri: `builder-<suffix>` (the owner's suffix, else the uri's file name without extension, e.g. `…/auth-plan.md` gives `builder-auth-plan`, else `builder-1`, `builder-2`, …). Find existing sessions with `mcp__claude-code-remote__list_sessions` (`mine: true`, `limit: 100`, page with `after_id`, ignore archived). A live idle session with the same title (`get_session` `status_bucket` other than `working` or `blocked`): archive it with `mcp__claude-code-remote__archive_session`, then recreate under the same title. Busy: next suffix (`-2`, `-3`, …).
2. **Session cap.** Count once for the whole request: live Fyrd sessions (you plus every live peasant) plus the new Builders, against `session_cap`. If over, call `AskUserQuestion` with `Start fewer now`, `Tear down idle sessions`, `Cancel` before creating any, and act as `fyrd-peasant` describes.
3. **Create.** Call `mcp__claude-code-remote__create_session` once per uri, in parallel, with:
   - `title` from step 1; `tags: ["fyrd-hub", "fyrd-role-builder"]`.
   - `environment_id` from `<hub-settings>` (omit when empty), `permission_mode: "auto"`, `model` from `<peasant-persona id="builder" …>`.
   - `append_system_prompt`: the builder persona text, a blank line, `<shared-memory>…</shared-memory>`, then if the role has memory a blank line and `<role-memory role="builder">…</role-memory>`.
   - `prompt`: `Build what the input below describes. Output: commit your work and push it to a new branch, then reply with the branch name and a summary. Do not open a pull request.` followed by the uri verbatim on its own line. If the owner asked for PRs, replace the last sentence with `Then open a pull request and reply with its link.`
   - `source_url` only when the request names the one repo to work in. The Builder attaches any repo it lacks itself (`add_repo` with `access: "push"`, clone, then `register_repo_root`).
4. Reply listing each Builder's title and session id, and stop. The owner reads results in the Builders' own sessions.
