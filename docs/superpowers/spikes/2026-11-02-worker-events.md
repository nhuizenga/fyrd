# Spike: how forked background workers appear in session events

**Status: captured** from an exported session (a Builder that forked one worker with `Agent`, `subagent_type: "fork"`). Sanitised events: `runtime/tests/Fyrd.Core.Tests/Fixtures/fork-worker-events.json`.

## Findings (saved events)

| Event | Shape |
|---|---|
| Parent `assistant` | `tool_use` `name: "Agent"`, `input: { description, prompt, subagent_type: "fork" }` (plus `run_in_background` when the persona sets it). |
| `system/background_tasks_changed` | `tasks: [{ task_id, task_type: "local_agent", subagent_type: "fork", description, run_id }]`; `tasks: []` once none is left. A ready-made "workers running" list. |
| `system/task_started` | `task_type: "local_agent"`, `subagent_type`, `description`, `prompt`, `is_backgrounded`, `spawn_depth`, `task_id`, `tool_use_id` (the `Agent` call's id). Bash tasks are `task_type: "local_bash"`. |
| Parent `user` tool_result | Only `"Async agent launched successfully … agentId …"`, **not** the result. |
| Worker's own messages | `assistant` events with `parent_tool_use_id` = the `Agent` call's id, `agent_id`, `subagent_type`, `task_description`. Saved, so a transcript must skip them. |
| `system/task_updated` | `patch: { status, end_time }`. |
| `system/task_notification` | `status`, `summary` (= the worker's final reply), `output_file`, `task_id`, `tool_use_id`. This is the completion signal. |
| `tool_progress` (from another export) | A main-agent Bash heartbeat carries `parent_tool_use_id` = the Bash call's own id, so don't treat "has a parent" as "is a worker". |

## What the console does with it

- `worker` item from `task_started` with `task_type: "local_agent"`; `worker-result` item from `task_notification` for a known worker (its `summary` and `status`). Bash tasks are ignored.
- Assistant events with `parent_tool_use_id` are skipped (previously a worker's reply would have shown as the peasant's text).
- The "workers running" chip uses the session tag; `background_tasks_changed` is a possible second source, not used yet.

## Still unverified

- Whether worker `stream_event`/`tool_progress` frames reach the ephemeral stream (no per-worker "latest tool" line yet).
- Whether `CostGuard` counts subagent usage.
- Playwright `console/e2e` mock for the chip and cards (Vitest covers them).
- `workerToolIds` lives for one SSE stream, so after a client reconnect the result of a worker started earlier is not shown as a `worker-result`.
