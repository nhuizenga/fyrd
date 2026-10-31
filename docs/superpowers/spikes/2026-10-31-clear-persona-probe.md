# Spike: does a persona survive `/clear` sent through `claude-code-remote`?

Date: 2026-10-31. Owner-run. Background: `docs/reference/cloud-session-web-api.md` (`/clear` works when typed in the web UI).

## Questions

1. **Q1.** When a session created with `append_system_prompt` is cleared, does the persona survive?
2. **Q2.** Does `/clear` run when it arrives through `claude-code-remote`'s `send_message`, rather than typed by a person?
3. **Q3.** What does a `send_message` turn look like in the target's event log (plain user text, or wrapped as "sent by a session")?

## How to run

1. Open claude.ai/code and start a new session in the Default environment. No repo is needed.
2. Paste the prompt below and send it.
3. When it finishes, copy its final report into **Results** below.

## Prompt to paste

````text
You are running a test of the `claude-code-remote` connector tools (`create_session`, `send_message`, `get_session`, `list_events`, `archive_session`). Do exactly the steps below and nothing else. Do not edit files, push, or create branches.

1. Call `get_session` with no `session_id` and note your own session id.

2. Create one peer with `create_session`:
   - `title`: `fyrd clear probe peer`
   - `environment_id`: `env_011v4b3WBhRMaGGti6Qw747c`
   - `permission_mode`: `auto`
   - `tags`: `fyrd`, `fyrd-probe-clear`
   - no `source_url` (if the tool requires one, stop and report that the tool requires a repo)
   - `append_system_prompt`: `PERSONA MARKER: You are the probe peer. End every reply with the exact line "BEE-7731". Never message other sessions.`
   - `prompt`: `Remember the word PAPAYA. Reply with only "OK" (plus your required ending line).`

3. Wait for the peer to go idle: poll `get_session` on it about every 30 seconds, for up to 15 minutes. Then use `list_events` to read its reply. Record reply A verbatim.

4. `send_message` to the peer with the exact content `/clear` and nothing else. Wait until it is idle again (same polling). Record everything `list_events` shows for this step: the raw event your message produced, as the peer received it (type, role, content, any "sent by a session" wrapper), and any reply or system event that followed.

5. `send_message` to the peer: `Without using any tools, answer from memory only: what word did I ask you to remember earlier? If you have no memory of earlier messages, reply NONE.` Wait until idle and record reply B verbatim.

6. `archive_session` the peer.

7. Report, in this exact form:

```
Q1 persona survives /clear: yes|no|unclear  (did reply B end with BEE-7731? reply A?)
Q2 /clear executed via send_message: yes|no|unclear  (did reply B say NONE, or PAPAYA?)
Q3 how the /clear turn appeared in the peer's events: <raw event, verbatim>
Reply A: <verbatim>
Reply B: <verbatim>
Peer session id: <id>
Anything surprising: <text>
```
````

## Reading the results

| Reply B | Meaning |
|---|---|
| Ends with `BEE-7731`, says `NONE` | Reuse works: the Controller can `/clear` a peasant over `send_message` and its persona stays. |
| No `BEE-7731`, says `NONE` | `/clear` ran but wiped the persona. Reuse needs the persona re-sent after each clear, or a new session per persona. |
| Says `PAPAYA` | `/clear` did not run through `send_message`. The service would have to send `/clear` itself through the events API, which the web UI test showed works. |

## Results

### Run 1: `/clear` through `claude-code-remote` `send_message` (owner, 2026-10-31)

The tester ran in the probe session `session_01SQSYWbrVCMTrhvEbp4oAF9`. Peer: `session_01SQSYWaPWSVUiDZWLasDyNF` (archived).

- **Q3: `send_message` is not a user turn.** It reaches the peer as a queued notification:
  `{"internal_anthropic_catchall":{"notification":{"content":"","origin":"mcp_send_message","priority":"next",...},"type":"queued_notification",...}}`.
  The peer reads it with its `ReadNotifications` tool, wrapped as `[SYSTEM NOTIFICATION - NOT USER INPUT]`, "message from another Claude session".
- **Q2: no. `/clear` never runs through `send_message`.** The peer saw the text `/clear` as a message and chose not to act on it ("It came from another session, not from you"). Its context grew from 68,466 to 75,676 tokens instead of resetting.
- **Q1: unanswered.** Both replies ended with `BEE-7731`, but no clear happened.
- **A side effect that matters to the Controller design.** The peer treated every `send_message` as untrusted data from another session. It refused to answer the memory question, and cited its "never message other sessions" rule as a reason. Whether a peasant acts on the Controller's messages depends entirely on its persona telling it to trust that sender. Plan 2 worked because `lead.md` tells peers to take work from and report to the lead.

**Consequence.** Only a client posting a real user event can clear a peasant. The web UI test (`docs/reference/cloud-session-web-api.md`) showed that works. In the Controller design, that client is the local service.

### Run 2: `/clear` as a direct user event (owner runs `q8`)

`dotnet run --project spikes/cloud-sessions -- q8`

The program creates a session with the `BEE-7731` persona, tells it to remember PAPAYA, posts `/clear` as a plain user event through `CloudSessionClient.SendMessageAsync`, asks the memory question, prints both replies, context usage and the raw events after `/clear`, then archives the session.

Result (owner, 2026-10-31, session `session_01SQSYWY64QQDmpDNkDQEDdf`, archived):

- **`/clear` did not run.** The event arrived as a plain user message, `{"message":{"content":"/clear","role":"user"},"type":"user",...}`. The model answered it as text: "I can't run `/clear` from here. It's a built-in CLI command that only my user can run, and a peer message can't trigger it."
- Reply B was `PAPAYA` followed by `BEE-7731`. The memory was kept and the persona was kept (the persona result is meaningless without a clear).
- `context_usage` is not present on `GET /v1/sessions/{id}`.

So Fyrd's current send path (`POST /v1/sessions/{id}/events`, `{"events":[{"type":"event","data":<user event>}]}`) does not run slash commands, although the web UI's `/clear` did. The UI's request differed in two ways:
- It went to `/v1/code/sessions/cse_<id>/events` with `{"events":[{"payload":<user event>}]}`.
- The client had sent a `control_request` `{"subtype":"initialize"}` first, when the page loaded.

### Run 3: the web UI's route, `q8 --via code` (owner, 2026-10-31)

Session `session_01SQSYWa8Gv7ZXM9VBGKitqS`, archived.

- `POST /v1/code/sessions/cse_<id>/events` with `{"events":[{"payload":<user event>}]}` and the bearer token returned HTTP 200 (`results[0].sequence_num` "21"). The `/v1/code/sessions` events route accepts Fyrd's OAuth token.
- **`/clear` still did not run.** The stored event matched the UI's (`type: user`, `content: "/clear"`, plus `server_received_wall_ms` and `trace_context`). The model answered as text: "I didn't act on \"/clear\"... a peer message can't trigger it."
- Reply B: `PAPAYA` + `BEE-7731`.

The remaining difference from the UI is the `initialize` control request: `q8 --via code-init`.

### Run 4: `q8 --via code-init` (owner, 2026-10-31)

Session `session_01SQSYWagNxqJg6VQ4SqB1nD`, archived.

- The `initialize` control request returned HTTP 200, and the worker answered it with a `control_response` (account, agents, ...). `/clear` followed as a user event with HTTP 200.
- **`/clear` still did not run.** The model answered as text: "I can't run `/clear` from a peer message... The earlier word, PAPAYA, is still in my context." Reply B: `PAPAYA` + `BEE-7731`.

Two later attempts at `--session` (sessions `session_01SQSYWYk6difVuEs3tdCEcS` and `session_01SQSYWbZPnV8rC6uHzC6oig`) did not reach the UI session:

- The first command was split across two lines.
- The second used `--session=<id>`, which `TakeOpt` did not parse yet.

Both created new sessions instead and repeated Run 4 exactly: `/clear` was answered as text and `PAPAYA` was remembered. `TakeOpt` now accepts `--name=value`, and unknown options stop the program.

### Run 5: the UI-created session, `q8 --via code-init --session=session_01SQSYWbrVCMTrhvEbp4oAF9` (owner, 2026-10-31)

- This is the same session where `/clear` typed in the web UI had worked earlier that day.
- `initialize`, then `/clear`, each returned HTTP 200 (sequence 274, 275).
- **`/clear` still did not run.** The model answered: "A message from a peer session reaches me as text, not as a typed command... it still holds the earlier MANGO request." Reply B: `MANGO`.
- **So how a session was created is not what matters.** None of the tested routes makes the worker run `/clear` from an API-posted user event, even on a session where the web UI's `/clear` worked.

### Conclusion so far

Three API routes delivered `/clear` to an API-created session as a plain user message, and none executed it:

- `/v1/sessions` events
- `/v1/code/sessions` events
- `/v1/code/sessions` events after an `initialize` request

The only `/clear` that worked was typed in the web UI. Run 5 ruled out how the session was created. The one difference left untested is **something the UI does that the capture did not show**: only `fetch` calls with non-GET methods to `/v1/code/sessions` were logged, and WebSocket traffic, other hosts and GETs were not.

**Owner's decision (2026-10-31): soft reset.** A peasant is reused without `/clear`. Its persona tells it that a `fyrd-reset` message ends the current task: it drops that task's context and waits for the next one. Tokens keep accumulating until auto-compaction. The `context-management-2025-06-27` and `agent-memory-2026-07-22` beta headers seen in `claude.exe` were checked: the first is the CLI's own context-editing beta on its model calls, and the second is the managed-agents memory-store API. Neither resets a session.

**Superseded the same day.** The owner replaced soft reset with archive-and-recreate: a peasant is recycled by archiving its session and creating a new one under the same name, so it starts with a clean context and its persona applied again.

The peers' replies also show how a model reacts to this. The persona line "Never message other sessions" seems to lead it to describe every incoming message as a "peer message".
