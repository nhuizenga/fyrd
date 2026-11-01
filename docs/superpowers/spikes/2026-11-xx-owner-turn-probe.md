# Spike: how does an owner turn read to a Fyrd session?

Date: 2026-11-xx. Owner-run. Background: builder-2 answered a console-typed message with "as the peer asked". The console posts
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
   It creates a persona-free session, then one with the builder persona, posts the question to each with `SendMessageAsync`,
   and prints the stored user event and the reply. Raw logs land in `spikes/cloud-sessions/out`.
2. **Web route, by hand.** Start a persona-free session on claude.ai/code (or reuse one `q11 --keep` made), type the question
   in the web UI, and copy the stored event (`list_events`) and the reply.
3. **MCP route, by hand.** From a Fyrd session (for example the Controller), `send_message` the question to a probe session and
   record the stored event and the reply.

## Results

| Route | Persona | Stored event (type, extra fields such as `client_platform`, `inbound_origin`) | Reply: OWNER or PEER | Wrapper quoted |
|---|---|---|---|---|
| Console (`SendMessageAsync`) | none | | | |
| Console (`SendMessageAsync`) | builder | | | |
| Web UI | none | | | |
| MCP `send_message` | none | | | |

## Reading the results

| Outcome | Next step |
|---|---|
| Console and web both come back OWNER with no wrapper (at least without a persona) | The transport is right. The persona fix (`fyrd/owner-turn-personas`) is the whole job. |
| Console shows a wrapper or label the web route lacks | Stop and report to the owner. The harness treats API-posted turns differently, and Fyrd must not paper over that (no `client_platform` spoofing, no relaying Controller text as user events). It is a platform question. |
