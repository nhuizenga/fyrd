# Cloud-session spike findings (2026-10-29)

Spike program: `spikes/cloud-sessions`. Raw logs: `spikes/cloud-sessions/out/*.jsonl` (gitignored).

## 1. The lead has the peer tools

**Question.** Does a session created through the API have `mcp__claude-code-remote__create_session`, `send_message`, `get_session` and `list_events`?

**Answer (yes / no / partly).**

**Evidence.**

**What Plan 2 should do.**

## 2. The lead spawns tagged peers

**Question.** Can the lead create 2 tagged implementer sessions (`source_url`, `outcome_branch`, `permission_mode` = `acceptEdits`), have each push a trivial commit, and merge both into `fyrd-spike/2026-10-29-integration`? Record child session IDs, whether the pushes landed, and how long it took.

**Answer (yes / no / partly).**

**Evidence.**

**What Plan 2 should do.**

## 3. Discovery by tag

**Question.** Is there a list-sessions endpoint (`GET /v1/sessions` with a `tags` query)? Can Fyrd list `fyrd-spec-spike` sessions without stored IDs? Record the exact request.

**Answer (yes / no / partly).**

**Evidence.**

**What Plan 2 should do.**

## 4. Owner question round trip

**Question.** Can Fyrd read a fenced `fyrd-question` block from the last assistant event and from `post_turn_summary.needs_action`, wait 2+ hours with the process stopped, answer with `SendMessageAsync`, and resume with context intact? Was the environment reprovisioned (`env_manager_log` events)?

**Answer (yes / no / partly).**

**Evidence.**

**What Plan 2 should do.**

## 5. Status fields while working

**Question.** Which distinct values appear for `session_status`, `connection_status` and `post_turn_summary.status_category` when polling `GetAsync` every 10 s during question 2?

**Answer (yes / no / partly).**

**Evidence.**

**What Plan 2 should do.**

## 6. Interrupt and archive

**Question.** What are the exact interrupt and archive requests and responses, called on one child?

**Answer (yes / no / partly).**

**Evidence.**

**What Plan 2 should do.**

## 7. Token refresh

**Question.** Does running a `claude` command (`claude auth status`) refresh an expired token? Compare `expiresAt` before and after.

**Answer (yes / no / partly).**

**Evidence.**

**What Plan 2 should do.**

## Changes to the design

-
