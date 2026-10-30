# Cloud-session spike findings (2026-10-29)

Spike program: `spikes/cloud-sessions`. Raw logs: `spikes/cloud-sessions/out/*.jsonl` (gitignored; this run's logs are in the `fyrd-foundation` worktree). The owner ran the spike between 19:01 and 19:19 local time (02:01–02:19 UTC).

Spike sessions (all tagged `fyrd-spec-spike`):

| Session | Role |
|---|---|
| `session_01SQSYWbVsxRJh9p8xiF1gqu` | q1 lead (later interrupted and archived by q6) |
| `session_01SQSYWZ1cYUK5w5DqC6RdET` | q2 lead |
| `session_01SQSYWXucrRTHhomaoFRvXf` | q2 implementer, unit a |
| `session_01SQSYWcGrANifJX63oTyNnH` | q2 implementer, unit b |
| `session_01SQSYWbHRMdBAkAcucQ81KM` | q4 lead |

## 1. The lead has the peer tools

**Question.** Does a session created through the API have `mcp__claude-code-remote__create_session`, `send_message`, `get_session` and `list_events`?

**Answer.** Yes.

**Evidence.** The q1 lead listed its `mcp__claude-code-remote__*` tools, including `create_session`, `get_session`, `interrupt_session`, `archive_session`, `add_repo` and the trigger tools (`q1.jsonl`). The q2 lead then actually used `create_session` (question 2), so this isn't only a self-report.

**What Plan 2 should do.** Rely on the lead creating and steering its peers itself, as the design (§5, §10) assumes.

## 2. The lead spawns tagged peers

**Question.** Can the lead create 2 tagged implementer sessions (`source_url`, `outcome_branch`, `permission_mode` = `acceptEdits`), have each push a trivial commit, and merge both into `fyrd-spike/2026-10-29-integration`?

**Answer.** Yes, but only after a human approved a permission prompt.

**Evidence.**
- The lead stopped at a permission prompt before its first `create_session` call. The spike's watcher saw no assistant replies and gave up after 88 s (`q2.jsonl`, `q2-timing.txt`). Once the owner approved the prompt in the web UI, the lead carried on.
- The lead created 2 implementer sessions at 02:16:51 and 02:16:53 UTC, with tags `fyrd-role-implementer`, `fyrd-spec-spike`, and `fyrd-unit-a` or `fyrd-unit-b` (`q3.jsonl`).
- All 3 branches landed on GitHub: `fyrd-spike/2026-10-29-a` (`72b6074`), `-b` (`e4a31c0`) and `-integration` (`c3250b3`).
- `gh api .../compare` shows both unit branches are fully contained in the integration branch (`behind_by=0`).
- Elapsed time is unknown, because the watcher had stopped. The children were created about 15 minutes after the lead, mostly spent waiting for approval.

**What Plan 2 should do.** Create every lead with a permission mode set up front, so it never stops to ask for approval. The CLI sends this event ahead of the first user message in the create body:
```json
{"type":"event","data":{"type":"control_request","request_id":"set-mode-<id>",
  "request":{"subtype":"set_permission_mode","mode":"<mode>"}}}
```
The modes are `default`, `plan`, `acceptEdits`, `dontAsk`, `bypassPermissions` and `auto`. A child session can be no more permissive than its lead. **The owner chose `auto` as the default (2026-10-29).** Plan 2 should also treat "lead is idle and waiting on a permission prompt" as an Inbox card, not as silence.

## 3. Discovery by tag

**Question.** Is there a list-sessions endpoint with a `tags` query? Can Fyrd list `fyrd-spec-spike` sessions without stored IDs?

**Answer.** Yes.

**Evidence.**
- `GET /v1/sessions?tags=fyrd-spec-spike&limit=100` returned only the tagged sessions: 2 on the first run, 5 on the second.
- An unfiltered `GET /v1/sessions?limit=100` returned 11, then 14.
- The `tags[]=` and `tag=` spellings are ignored, and return everything.
- Each session in the list response carries `id`, `tags`, `title`, `session_status` and `created_at`.

**What Plan 2 should do.** Discover a spec's sessions with `GET /v1/sessions?tags=fyrd-spec-<specId>`. Add `ListAsync(string tag)` to `CloudSessionClient`. Paging is `has_more`; none of the runs needed a second page.

## 4. Owner question round trip

**Question.** Can Fyrd read a fenced `fyrd-question` block from the last assistant event and from `post_turn_summary.needs_action`, wait 2+ hours with the process stopped, answer with `SendMessageAsync`, and resume with context intact?

**Answer.** Partly. The round trip works, but the 2-hour wait has not been tested.

**Evidence.**
- The q4 lead ended its turn with a fenced `fyrd-question` block: `{"id":"q-color","question":"Which colour should the spike use, red or blue?","options":["red","blue"]}`.
- After the answer, it replied `PINEAPPLE-7421 blue`, so the codeword from its first turn survived.
- The answer was sent about 1 minute after the question, not 2+ hours (`q4-state.json`, `q4.jsonl`).
- The lead also called a read-only session-listing tool despite being told to use none, and said so.

**What Plan 2 should do.**
- Parse `fyrd-question` blocks from the last assistant event, as the design specifies.
- Don't rely on a "use no tools" instruction being followed.
- **Still open:** run `q4 ask`, wait 2+ hours, then `q4 answer <id>` before Plan 2 depends on long waits. The docs say an idle session's VM is reclaimed after inactivity and the conversation is restored on the next message, so the answer should still work; the spike should confirm that.

## 5. Status fields while working

**Question.** Which distinct values appear for `session_status`, `connection_status` and `post_turn_summary.status_category`?

**Answer.** Partly. q5 was not run while sessions were working.

**Evidence.** Values seen across q2, q3, q4 and q6: `session_status` = `idle`; `connection_status` = `connected` or `disconnected`; `status_category` = `completed`. No `working` value was captured, because nobody polled while a session was busy.

**What Plan 2 should do.** Map `idle` with `completed` to "done or waiting", and anything else to "working". Log any status value it hasn't seen before, and show it as-is on the Board rather than guessing. This is cheap to refine once real values show up.

## 6. Interrupt and archive

**Question.** What are the exact interrupt and archive requests and responses?

**Answer.** Yes, both work. They were called on the q1 lead, not a child, because the owner passed that ID.

**Evidence.**
- Interrupt is `POST /v1/sessions/{id}/events` with body `{"events":[{"type":"control_request","request_id":"<uuid>","request":{"subtype":"interrupt","cancel_queued":true}}]}`. It returned 200.
- Archive is `POST /v1/code/sessions/{id}/archive` with body `{}`. It returned 200 with the session's config, and the session then showed `external_metadata.archive_container_stop_pending_epoch` (`q6.jsonl`).
- Archive uses the `/v1/code/sessions` path; the other calls use `/v1/sessions`.

**What Plan 2 should do.** Add `InterruptAsync` and `ArchiveAsync` to `CloudSessionClient` with exactly these shapes. Archive finished peer sessions to release their containers.

## 7. Token refresh

**Question.** Does running a `claude` command refresh an expired token?

**Answer.** Inconclusive. The token wasn't near expiry.

**Evidence.**
- `expiresAt` was `2026-10-30T07:21:10Z` both before and after `claude auth status`.
- The API probe returned 200 both times.
- `claude auth status` reports `loggedIn: true`, `authMethod: claude.ai` (`q7.jsonl`).

**What Plan 2 should do.** Keep the design §11 behavior: on 401, show a banner asking the owner to run `claude auth login`. Retest q7 when the token is within minutes of expiry.

## Changes to the design

- **§10, server-side coordination:** each lead is created with an explicit permission mode (a `set_permission_mode` control event ahead of the first user message), so it never blocks on an approval prompt. Default mode: `auto` (owner's choice).
- **§10, escalation protocol:** a lead that is idle on a permission prompt becomes an Inbox card.
- **§11, endpoints:** add the verified list-by-tag (`GET /v1/sessions?tags=`), interrupt (`POST /v1/sessions/{id}/events` with a `control_request` interrupt event) and archive (`POST /v1/code/sessions/{id}/archive`) calls.
- **Still to verify before relying on long waits:** q4 with a real 2+ hour gap, and q7 near token expiry.
