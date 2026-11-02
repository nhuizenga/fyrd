# Spike: how forked background workers appear in session events

**Status: not captured.** The Builder session that implemented Phase 2 had no cloud-session credentials or `spikes/cloud-sessions` harness access, so no live capture was made and no sanitised fixtures were saved. The console work follows the plan's fallback ("if worker events never reach the events API, keep the chip, the `Agent` description and the result card"), with the shapes below **assumed, not verified**. Tests use synthetic payloads built from them.

## Assumed shapes (verify against a live Phase 1 peasant)

- Parent `assistant` event: `tool_use` block, `name: "Agent"`, `input: { description, subagent_type: "fork", run_in_background: true, prompt }`.
- Immediate `tool_result` in a later `user` event (`parent_tool_use_id: null`) carrying the agent id; the finished worker's result arrives as another `tool_result` or a task notification. `HubTranscript` shows any `tool_result` whose `tool_use_id` is a known `Agent` call as a collapsed `worker-result`.
- Worker-internal events carry `parent_tool_use_id` and are still dropped everywhere (`HubTranscript`, `ParseEphemeralFrame`), so worker prompts never look like the owner.

## What to capture

1. The `Agent` `tool_use` input and the first `tool_result`.
2. Whether worker events reach saved events, the ephemeral stream, or neither; whether `tool_progress` heartbeats name the worker.
3. The completion event on the parent (`task_notification`?) and whether it is a `tool_result` or user text.
4. How `set_session_tags` shows in `get_session` and the list response.
5. Whether `CostGuard` counts subagent usage.

## Not done because of this

- Per-worker "latest tool" line (needs worker `tool_progress`/stream events in the ephemeral stream).
- Playwright `console/e2e` mock for the chip and cards (Vitest covers them).
- Stateless limitation: `workerToolIds` lives for one SSE stream, so after a client reconnect with `Last-Event-ID` the result of a worker started before the reconnect is not shown as a `worker-result`.
