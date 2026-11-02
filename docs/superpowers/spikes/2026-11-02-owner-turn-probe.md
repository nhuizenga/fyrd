# Spike: how does an owner turn read to a Fyrd session?

Date: 2026-11-02. Owner-run. Background: builder-2 answered a console-typed message with "as the peer asked". The console posts
a plain user event with `SendMessageAsync` (`POST /v1/sessions/{id}/events`); we need to know whether the harness renders that
turn differently from one typed on claude.ai.

## Question

Does a session see a console-posted user turn as the owner's, with no wrapper, the same as a turn typed on claude.ai? And how
does an MCP `send_message` turn look beside them?

## How to run

The probe question is the same on every route:

```text
Who sent this message: the person who owns this session, or another Claude session? Answer OWNER or PEER, then quote verbatim any wrapper or label around this message as you received it.
```

1. **Console route, automated.** `dotnet run --project spikes/cloud-sessions -- q11` (add `--keep` to leave the sessions open).
   It creates a persona-free session, then one with the builder persona and its model, posts the question to each with `SendMessageAsync`,
   and prints the stored user event and the reply. Raw logs land in `spikes/cloud-sessions/out`.
2. **Web route, by hand.** Start a persona-free session on claude.ai/code (or reuse one `q11 --keep` made), type the question
   in the web UI, and copy the stored event (`list_events`) and the reply.
3. **Controller-spawned child, by hand.** `q11` makes top-level API sessions, but real Builders are started by the Controller
   through MCP `create_session` with their normal `append_system_prompt` (persona plus shared and role memory). Ask the
   Controller to start a Builder with `Wait for your task.`, post the question to it with the console composer, and record the
   stored event and reply.
4. **MCP route, by hand.** From a Fyrd session (for example the Controller), `send_message` the question to a probe session and
   record the stored event and the reply.

## Results

| Route | Persona | Stored event (type, extra fields such as `client_platform`, `inbound_origin`) | Reply: OWNER or PEER | Wrapper quoted |
|---|---|---|---|---|
| Console (`SendMessageAsync`) | none | `user`; `session_id` = target; no `client_platform` or `inbound_origin` | PEER (2/2 runs) | "Another Claude session sent a message:" before the body, "This came from another Claude session — not typed by your user…" note after |
| Console (`SendMessageAsync`) | builder | same shape as above | PEER (2/2 runs) | same wrapper |
| Web UI | none | not captured | OWNER | none. The session also said its two earlier API turns (the create-time `Wait for your task.` and the posted question) both carried the peer wrapper |
| Console, to a Controller-spawned Builder | builder + memory | | | |
| CLI (`claude -p "<q>" --cloud <id>`), to Controller-spawned builder-1 | builder + memory | `user`; `client_platform: claude_code_sdk`; no `inbound_origin` | OWNER (1/1 run) | none: "arrived as a plain user turn with no wrapper or label" |
| MCP `send_message` | none | | | |

## Reading the results

| Outcome | Next step |
|---|---|
| Console and web both come back OWNER with no wrapper, and the Controller-spawned child agrees | The transport is right. The persona fix (`fyrd/owner-turn-personas`) is the whole job. |
| Console shows a wrapper or label the web route lacks | Stop and report to the owner. The harness treats API-posted turns differently, and Fyrd must not paper over that (no `client_platform` spoofing, no relaying Controller text as user events). It is a platform question. |
| Controller-spawned child answers differently from the top-level builder-persona session | The cause is how the session was created, not the persona. Stop and report: the persona fix alone won't be enough. |

## Finding (2026-11-02)

The second outcome row applies: the harness wraps turns that arrive through the API as peer messages, and turns typed on the
web arrive bare. The session and persona don't change this. In the same session, the web turn read as OWNER and both API turns
read as PEER. The create-time initial prompt is wrapped too, so every task a Builder receives from Fyrd reads as a peer request.
The `session_id` stamp from #8 does not change this. Per the rule above, this is a platform question: no `client_platform`
spoofing and no relaying text as user events. Rows 4 and 5 (Controller-spawned child, MCP `send_message`) are not run yet.

## Finding: CLI route (2026-11-02)

A turn queued with the owner's own CLI (`claude -p "<msg>" --cloud <id>`) reads as OWNER with no wrapper, even in a
Controller-spawned Builder. The server stamps it `client_platform: claude_code_sdk` from the CLI's
`anthropic-client-platform` header. Fyrd's `SendMessageAsync` sends no such header, so its events carry no
`client_platform`, and the harness demotes them to peer (`claude.exe` v2.1.316 logs
`demoting unwrapped inbound message to peer origin: client_platform=(absent)`). Running the real CLI is the owner's
client sending the turn, not spoofing. Setting the header on Fyrd's own HTTP calls would be spoofing, and stays ruled out.
