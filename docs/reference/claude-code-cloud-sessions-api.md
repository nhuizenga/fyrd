# Claude Code cloud sessions: undocumented API reference

Everything Fyrd knows about driving Claude Code cloud sessions (the sessions at claude.ai/code) from code. None of this is a public API. It comes from three sources:

- **Verified:** observed with real HTTP calls, through the probes in `spikes/cloud-cli/` and `spikes/cloud-sessions/`.
- **Read from the CLI:** taken from strings and code in the `claude` CLI binary (`claude.exe` v2.1.312, read 2026-10-29 to 2026-10-31). Not yet called by Fyrd.
- **Tool docs:** the `claude-code-remote` MCP tool descriptions, as available inside a cloud session.

Every section says which source it rests on. Anthropic can change any of this in any Claude Code release. Fyrd keeps every call in one class, `CloudSessionClient`, so a break is a single-file fix.

Related:
- `docs/reference/cloud-session-web-api.md`: the web UI's calls, file uploads, `/clear`
- `docs/reference/mcp__claude-code-remote.json`: full `claude-code-remote` tool schemas
- `docs/superpowers/specs/2026-10-29-fyrd-design.md` §11
- `docs/superpowers/spikes/2026-10-29-cloud-sessions.md`
- `docs/superpowers/spikes/2026-10-30-plan2-smoke.md`

---

## 1. Authentication (verified)

| Header | Value |
|---|---|
| `Authorization` | `Bearer <claude.ai OAuth access token>`, from `~/.claude/.credentials.json`, `claudeAiOauth.accessToken` |
| `x-organization-uuid` | from `~/.claude.json`, `oauthAccount.organizationUuid` |
| `anthropic-version` | `2023-06-01` |
| `anthropic-beta` | `ccr-byoc-2025-07-29`. Required on `/v1/sessions…`; **not** needed on `/v1/code/sessions/{id}/events/stream` |

- The token's scopes include `user:sessions:claude_code`.
- **Expiry:** `claudeAiOauth.expiresAt` is in epoch milliseconds. Running any `claude` command refreshes the token; the 2026-10-29 spike didn't get to observe that (q7 inconclusive).
- **Trusted-device token** (read from the CLI): the CLI's stream client also sends a `td-v1` header when one is available. Some organizations need it; Fyrd's calls haven't.
- **Never log, store, or forward the token.**

## 2. Two API families

From the CLI code (`RJo`):

| Family | Base path | Headers | Used by |
|---|---|---|---|
| v1 | `/v1/sessions` | `anthropic-beta: ccr-byoc-2025-07-29` | `claude --cloud` (classic path), **Fyrd's `CloudSessionClient`** |
| v1alpha2 | `/v1/code/sessions` | auth only | the newer CLI client (`SessionsV2Client`), the Remote Control bridge |

Both address the same sessions. A session has two ID forms with the same suffix:
- `session_…`: used for status reads, and everywhere in v1.
- `cse_…`: used on the web UI's `/v1/code/sessions/cse_<id>/events` calls.

The web UI creates sessions with `POST /v1/code/sessions`, putting a `config` object where v1 uses `session_context` (see `cloud-session-web-api.md`). One exception to the shared surface: archive only exists under `/v1/code/sessions`.

## 3. Endpoints

### 3.1 Verified (used by Fyrd)

| Purpose | Call | Notes |
|---|---|---|
| Create | `POST /v1/sessions` | Body below (§4). Returns the session JSON with `id`. |
| Get | `GET /v1/sessions/{id}` | `session_status` (`idle`, `running`, `archived`, …), `connection_status` (`connected`, `disconnected`), `external_metadata` (§6). |
| Events (paged) | `GET /v1/sessions/{id}/events?limit=&after_id=&before_id=` | `{data, has_more, first_id, last_id}`. Events are the same shapes as the stream's `client_event` payloads (§5.3). |
| Send | `POST /v1/sessions/{id}/events` | `{"events":[<user event>]}`. The user event is **not** wrapped in `type:"event"`. |
| List by tag | `GET /v1/sessions?tags=<tag>&limit=100` | Filters on the server. `tags[]=` and `tag=` are **ignored** and return everything. Page with `has_more` / `after_id`. |
| Interrupt | `POST /v1/sessions/{id}/events` | `{"events":[{"type":"control_request","request_id":"<uuid>","request":{"subtype":"interrupt","cancel_queued":true}}]}` |
| Archive | `POST /v1/code/sessions/{id}/archive` | Body `{}`. The session then shows `external_metadata.archive_container_stop_pending_epoch`. |
| Unarchive | `POST /v1/code/sessions/{id}/unarchive` | Body `{}`. Returns `{session:{…}}` (verified 2026-11-02). |
| **Event stream** | `GET /v1/code/sessions/{id}/events/stream` | Server-Sent Events (§5). Verified 2026-10-31. |

### 3.2 Seen in the CLI, not yet called by Fyrd

| Call | Purpose (inferred) |
|---|---|
| `POST /v1/code/sessions` | The v1alpha2 create; the web UI uses it. Body `{title, tags, environment_id, events, config:{append_system_prompt, model, effort_level, sources, outcomes}}` (observed by the web UI capture) |
| `POST /v1/code/sessions/{id}/events` | The v1alpha2 send. Events are wrapped as `{"payload":{…}}`. The response carries `results[0].sequence_num`. User events may carry `file_attachments` (see `cloud-session-web-api.md`) |
| `POST https://claude.ai/api/<orgUuid>/upload` | File upload; returns a `file_uuid` (observed by the web UI capture, with cookie auth) |
| `GET /v1/code/sessions?limit=1` | List (v1alpha2) |
| `GET /v1/code/sessions/watch?include_trigger_sessions=true&exclude_tags=…` | Watch the session *list* (Remote Control bridge) |
| `POST /v1/code/sessions/{id}/worker/register`, `…/worker`, `…/worker/events`, `…/worker/diagnostics`, `…/remote` | Self-hosted (BYOC) workers |
| `/v1/environments`, `/v1/environments/{id}`, `/v1/environment_providers`, `/v1/environment_providers/cloud/create` | Environments |
| `/v1/environments/bridge`, `/v1/environments/{id}/bridge/reconnect`, `/v1/environments/{id}/work/{id}`, `…/heartbeat`, `…/tool_hosts/refuse` | Remote Control bridge and work leases |
| `GET /v1/sessions/{id}/events/stream?beta=true`, `GET /v1/sessions/{id}/threads/{id}/stream?beta=true` | **Managed Agents** streams (bundled Anthropic SDK, beta `managed-agents-2026-04-01`). Not Claude Code's. |

## 4. Creating a session (verified)

`POST /v1/sessions`:

```json
{
  "title": "Fyrd lead · <spec title>",
  "tags": ["fyrd", "fyrd-spec-<specId>", "fyrd-role-lead"],
  "environment_id": "env_… | ccpool_…",
  "events": [
    {"type":"event","data":{"type":"control_request","request_id":"set-mode-<uuid>",
      "request":{"subtype":"set_permission_mode","mode":"auto"}}},
    {"type":"event","data":{"uuid":"<uuid>","session_id":"","type":"user","parent_tool_use_id":null,
      "message":{"role":"user","content":"<first message>"}}}
  ],
  "session_context": {
    "sources": [{"type":"git_repository","url":"https://github.com/<owner>/<repo>","revision":null}],
    "outcomes": [],
    "environment_variables": {},
    "model": "claude-opus-5-5",
    "append_system_prompt": "<optional>"
  }
}
```

- **Permission modes:** `default`, `plan`, `acceptEdits`, `dontAsk`, `bypassPermissions`, `auto`.
  - Without a mode event, a lead stops at its first tool-permission prompt. The spike's q2 hit this.
  - Fyrd always sends `auto`.
- **Fields the CLI also sends** (seen in its create body; not used by Fyrd): `seed_bundle_file_id` (for uploads with no GitHub remote), `custom_system_prompt`, `max_turns`, `output_schema`, `correlation_id`.

## 5. The event stream (verified 2026-10-31)

`GET /v1/code/sessions/{id}/events/stream` with `Accept: text/event-stream` and the auth headers from §1 (no beta needed).

### 5.1 Resuming
- **From the start:** with no `from_sequence_num`, the stream **replays every saved event from #1**, then stays open and delivers new events live.
- **From a known point:** `?from_sequence_num=N` starts **after** N; N is exclusive. The CLI also sends `Last-Event-ID: N`.
- **Duplicates:** the CLI drops any sequence number it has already seen. Do the same.
- **Control only** (read from the CLI): `?control_only=1` limits the stream to control frames.
- **Long-poll behavior** (read from the CLI): the server may end the stream on purpose. The client reopens it at once, and also reopens a connection that stays silent too long.

### 5.2 Frame types

Each frame has `event:`, optionally `id:` (the sequence number), and `data:` (JSON).

| `event:` | `id:` | `data` | Notes |
|---|---|---|---|
| `client_event` | sequence number | `{event_id, event_type, payload, sequence_num, created_at, source, …}`; client-sent events also carry `received_at`, `processing_at`, `processed_at`, `device_attestation_status` | Saved; part of the transcript; replayed |
| `ephemeral_event` | none | `{event_type, payload, source, timestamp}` | Live only; never replayed |
| `delivery_update` | none | `{event_id, status, timestamp}` | Delivery state of an event you sent |
| `session_update` | none | `{connection_status}` | First frame on open |
| `keep_alive`, `connected` | none | — | Housekeeping (read from the CLI; not seen in the 10 s probe) |

### 5.3 `client_event` payload types (observed)

| `payload.type` | Subtypes seen | Meaning |
|---|---|---|
| `user` | — | A user message, or tool results |
| `assistant` | — | A model message. The text is in `message.content[].text` |
| `result` | `success` | **End of a turn** |
| `system` | `init`, `status`, `turn_handoff_available`, `background_tasks_changed` | Runtime state |
| `control_request` | `set_permission_mode`, `initialize`, `mcp_set_servers`, `mcp_message`, `get_context_usage`, `interrupt` (sent by Fyrd) | Control-plane requests. **Permission prompts are expected to arrive here; not yet observed.** |
| `control_response` | — | Replies to control requests |
| `env_manager_log` | — | Sandbox setup log: allocate, clone, setup script, start |
| `rate_limit_event` | — | Account rate-limit state |
| `autocompact_state` | — | Context compaction state |
| `prompt_suggestion` | — | A suggested next prompt, shown in the UI |

### 5.4 `ephemeral_event` payload types (observed)

| `payload.type` | Subtypes | Meaning |
|---|---|---|
| `stream_event` | — | Partial model output while a turn is in progress |
| `system` | `post_turn_summary` | **Status summary at the end of a turn**: the same data as `external_metadata.post_turn_summary` |
| `system` | `task_summary`, `status`, `hook_started`, `hook_response` | Progress and hook activity |

### 5.5 What this means for Fyrd (not yet built)
- **One stream per active session** can replace the 10-second polling in `SpecRunner`. Resume from the last `sequence_num` after any drop.
- **Use `result` to detect the end of a turn,** and `post_turn_summary` for the status, instead of inferring both from `session_status` and `needs_action`.
- **Permission prompts:** watch `control_request` for an approval subtype. Until one is observed, keep the current attention-card fallback.

## 6. Session status fields (verified)

`GET /v1/sessions/{id}` includes:

- `session_status`: values seen are `idle`, `running`, `archived`.
- `connection_status`: values seen are `connected`, `disconnected`.
- `external_metadata`:
  - `post_turn_summary`: `{status_category, status_detail, recent_action, needs_action}`. The only `status_category` value seen so far is `completed`.
  - `context_usage`: `{used_tokens, max_tokens}`.
  - `rate_limit_info`: `{status, resetsAt (epoch seconds), rateLimitType, isUsingOverage}`.
  - `autocompact_state`, `current_branches`, `last_served_model`, `container_cc_version`.
  - `turn_handoff.tools`: the tool list, including the `mcp__claude-code-remote__*` tools (§8).
- `configured_model`, `environment_id`, `environment_kind` (`anthropic_cloud`), `created_at`, `tags`, `title`.

### 6.1 Cost fields (used by Fyrd's spend limits)

Both are **list-price estimates** (`costBasis: "list"`), not billed amounts. Seen on a live session on 2026-11-01 through the `claude-code-remote` MCP tools; Fyrd's own REST client has **not yet captured** a raw body, so `runtime/tests/Fyrd.Core.Tests/Fixtures/result-event.json` is a trimmed reconstruction of that shape (top-level fields), not a capture. Fyrd reads the fields wherever they sit: on the event, or one level down in `internal_anthropic_catchall`, `payload`, `data` or `event`.

| Where | Field | Meaning | Fyrd uses it for |
|---|---|---|---|
| `result` event (end of a turn) | `model_usage_since_last_result.<model>.costUSD` | Cost of that turn per model; Fyrd sums the models | Completed spend, dated by the event's `created_at` in the configured time zone |
| `result` event | `total_cost_usd` | Running total for the container process; resets when the worker restarts | Fallback only: the delta between consecutive results when the per-turn map is missing (a drop means a restart) |
| `GET /v1/sessions/{id}` | `external_metadata.usage.cost_usd` (plus token counts) | Cumulative session cost, including the turn in progress | In-flight estimate: `max(0, cost_usd − sum of result costs seen)` |

Open: whether the `GET /v1/sessions?tags=` listing items carry `external_metadata.usage` (Fyrd reads it from the listing items and shows "—" with no enforcement when it and the result costs are both absent; if the listing lacks it, fetch with `GET /v1/sessions/{id}` per session).

## 7. CLI paths (verified)

| Command | Behavior |
|---|---|
| `claude --cloud "<task>"` | Creates a session for the current repo. **Requires an interactive terminal**: a program can drive it through a pseudo-terminal (`spikes/cloud-cli/PtyProbe`), but not when its output is piped. `-p` with a task is rejected. |
| `claude -p "<msg>" --cloud <id> [--output-format json]` | Queues one message, then exits without waiting for a reply. JSON output is `{ok, session_id, url}`. |
| `<msg on stdin> \| claude -p --cloud <id> [--output-format json]` | Same, reading the message from stdin. Delivered byte-for-byte: newlines, quotes, `$`, backticks, `& \| < > ^ %`, non-ASCII (verified 2026-11-02). The turn is stamped `client_platform: claude_code_sdk` and reads as the owner's, with no peer wrapper. An archived session fails with `{ok:false, error:"… is archived and cannot accept new messages"}` and exit 1. |
| `claude --cloud <id>` (without `-p`) | Fails with "Attaching to an existing cloud session is not enabled for your account." |
| `claude -p --teleport <id> "<prompt>"` | Copies the cloud transcript to a local JSONL file and runs one model turn on it. This costs a model turn, and needs a clean local clone of the repo. |
| `claude agents --json` | Lists **local** sessions only; cloud sessions don't appear. |
| `--environment <ccpool_…>` | Creates a session on a **self-hosted** environment |

## 8. `claude-code-remote` MCP tools (tool docs; partly verified)

Available inside cloud sessions, including sessions created through the API (verified in the spike's q1). The ones Fyrd's personas rely on:

| Tool | What Fyrd uses it for |
|---|---|
| `get_session` | With `session_id` omitted, it describes **the calling session**. This is how a lead learns its own ID. Verified in the Plan 2 smoke run. |
| `create_session` | `prompt`, `source_url`, `source_revision`, `environment_id`, `model`, `title`, `tags`, `permission_mode` (no more permissive than the caller's), `extra_allowed_tools`, `append_system_prompt`, `outcome_branch`, `clone_depth`, `blob_limit_kb`, `sparse_checkout_paths` |
| `send_message` | Arrives at the target as a new turn, marked as sent by a session; **it wakes an idle session**. Verified in the Plan 2 smoke run. |
| `list_events`, `get_event` | Read a peer's transcript |
| `interrupt_session`, `archive_session`, `unarchive_session` | Lifecycle |
| `list_sessions`, `set_session_tags`, `set_session_title` | Bookkeeping |

The tool docs also list scheduled-task tools (`create_trigger`, `send_later`, `watch_url`, …), repository tools (`add_repo`, `check_repo_access`, …), and computer-folder tools.

## 9. Git branches (verified)

- **No nested branch names.** A branch `X` and a branch `X/y` can't coexist in git. A session whose `outcome_branch` would nest under an existing branch fails at "setting up the repository" (Plan 2 smoke, run 1).
- **Fyrd's naming:** `fyrd/<specId>/integration` and `fyrd/<specId>/unit/<unitId>`.
- Sessions push through Anthropic's GitHub proxy, which uses the owner's GitHub connection.

## 10. Strings in `claude.exe` (v2.1.312)

### 10.1 Beta-flag-shaped strings
A quoted lowercase name ending in a date. Not every one is sent in `anthropic-beta`, and names built at runtime won't appear in this list.

- **Sessions and platform:**
  - `ccr-byoc-2025-07-29`
  - `managed-agents-2026-04-01`
  - `environments-2025-11-01`
  - `ccr-triggers-2026-01-30`
  - `agent-memory-2026-07-22`
  - `mcp-tunnels-2026-06-22`
  - `mcp-servers-2025-12-04`
  - `user-profiles-2026-08-18`
  - `files-api-2025-04-14`
  - `oauth-2025-04-20`
  - `oidc-federation-2026-04-01`
- **Thinking:**
  - `interleaved-thinking-2025-05-14`
  - `redact-thinking-2026-02-12`
  - `thinking-binding-controls-2026-08-01`
  - `thinking-display-updates-2026-08-18`
  - `thinking-resumption-2026-07-17`
  - `thinking-token-count-2026-05-13`
- **Context and caching:**
  - `context-1m-2025-08-07`
  - `context-management-2025-06-27`
  - `context-hint-2026-04-09`
  - `extended-cache-ttl-2025-04-11`
  - `prompt-caching-scope-2026-01-05`
  - `prompt-caching-evict-2026-05-12`
  - `cache-keepalive-2026-09-03`
  - `cache-diagnosis-2026-04-07`
- **Mid-conversation:**
  - `mid-conversation-system-2026-04-07`
  - `mid-conversation-system-clear-at-2026-08-21`
  - `mid-conversation-tool-changes-2026-07-01`
  - `per-turn-control-2026-07-01`
  - `message-threads-2026-08-12`
- **Tools:**
  - `advanced-tool-use-2025-11-20`
  - `tool-search-tool-2025-10-19`
  - `inline-tools-2026-09-15`
  - `advisor-tool-2026-03-01`
  - `dangerous-tool-use-2026-09-03`
  - `web-search-2025-03-05`
  - `structured-outputs-2025-12-15`
- **Model and runtime:**
  - `effort-2025-11-24`
  - `fast-mode-2026-02-01`
  - `task-budgets-2026-03-13`
  - `server-side-fallback-2026-06-01`
  - `server-side-fallback-2026-07-01`
  - `fallback-credit-2026-06-01`
  - `auto-mode-classifier-2026-07-16`
  - `afk-mode-2026-01-31`
  - `dreaming-2026-04-21`
  - `timing-2026-09-09`
- **Other:**
  - `message-batches-2024-09-24`
  - `token-counting-2024-11-01`
- **Not betas** (provider API versions):
  - `bedrock-2023-05-31`
  - `vertex-2023-10-16`

### 10.2 `byoc` strings
BYOC appears to mean "bring your own compute": self-hosted environments (`ccpool_…`) and their workers.

- **Kinds and flags:** `byoc`, `not_byoc`, `ccr_byoc`, `env_byoc`, `environment_byoc`
- **Worker lifecycle:** `byoc_register_worker`, `byoc_whoami`, `byoc_work_ack`, `byoc_work_stop`, `byoc_session_execute`
- **Setup steps:** `byoc_git_prepare_repo`, `byoc_no_git_source`, `byoc_task_setup_script`, `byoc_env_skip_preflight`
- **Telemetry:** `CCR_BYOC_RESOURCE_WATCHDOG_TELEMETRY`
- **Only in an older build** (likely a removed experiment): `ccr-byoc-prefetch-network.state`, `ccr-byoc-prewarm-{idx,stat,vda}.done`

## 11. Open questions

1. **Permission prompts:** the `control_request` subtype a pending tool-permission prompt produces, and how to answer it (a `control_response`?).
2. **Long waits:** whether a session survives a 2+ hour wait on an answer (spike q4, deferred).
3. **Token refresh:** behavior near expiry (spike q7).
4. **Status values while busy:** all values of `session_status` and `status_category` while a session is working (spike q5).
5. **The trusted-device header:** when `td-v1` is required.
