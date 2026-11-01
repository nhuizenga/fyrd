# Cloud sessions: file uploads and /clear (web UI API)

Observed 2026-10-31 by capturing the claude.ai/code web UI's network calls while attaching a file to a prompt. None of this is documented by Anthropic and it can change in any release. Treat it the same as the `/v1/sessions` API in `docs/superpowers/specs/2026-10-29-fyrd-design.md` §11.

Probe session: `session_01SQSYWbrVCMTrhvEbp4oAF9` (title "Fyrd upload probe", Default environment, no repo).

## Summary

A file reaches a cloud session in two calls:

1. Upload the file to claude.ai, which returns a `file_uuid`.
2. Send a user event that lists the `file_uuid` in `file_attachments`.

The session's container gets the file under `/root/.claude/uploads/`, and Claude reads it from there.

Attaching a file in the UI makes no network call. The upload happens when the prompt is sent.

## 1. Upload

`POST https://claude.ai/api/<orgUuid>/upload`

- Body: `multipart/form-data` with one field, `file` (the browser sent `text/markdown` for a `.md` file).
- Auth: the browser sends its claude.ai cookies. **Unverified:** whether the OAuth bearer token Fyrd uses for `/v1/sessions` is accepted here.
- Response (200):

```json
{
  "success": true,
  "file_uuid": "21beca2f-c440-4659-990f-a1eb46f3472b",
  "uuid": "21beca2f-c440-4659-990f-a1eb46f3472b",
  "file_name": "controller.md",
  "file_kind": "blob",
  "media_type": "text/markdown",
  "size_bytes": 2006,
  "created_at": "<timestamp>",
  "path": null,
  "sanitized_name": null,
  "user_uuid": null
}
```

`uuid` and `file_uuid` were equal in the observed response.

## 2. Reference the file in a message

`POST /v1/code/sessions/<cse_id>/events`

The user event carries `file_attachments` next to `message`:

```json
{
  "events": [
    {
      "payload": {
        "type": "user",
        "uuid": "<new uuid>",
        "session_id": "<session id>",
        "parent_tool_use_id": null,
        "message": { "role": "user", "content": "<prompt text>" },
        "file_attachments": [
          {
            "file_uuid": "<from the upload response>",
            "file_name": "README.md",
            "mime_type": "text/markdown",
            "is_image": false
          }
        ]
      }
    }
  ]
}
```

On the first message of a new session, the UI puts a `control_request` event before the user event (`request.subtype` plus `request.mode`, i.e. the permission mode). This matches the `set_permission_mode` finding in `docs/superpowers/spikes/2026-10-29-cloud-sessions.md`.

## 3. Where the file lands

Claude in the session read the first upload at:

```
/root/.claude/uploads/6ca4e69f-54fd-5253-b778-8806cc9b674f/99ca0199-README.md
```

A second upload (`controller.md`, `file_uuid` `21beca2f-c440-4659-990f-a1eb46f3472b`) sent to the same session later landed at:

```
/root/.claude/uploads/6ca4e69f-54fd-5253-b778-8806cc9b674f/21beca2f-controller.md
```

So the pattern is `/root/.claude/uploads/<per-session folder uuid>/<first 8 hex of file_uuid>-<file name>`:

- The folder uuid stayed the same for both uploads, so it is per session (it does not match the session id).
- The prefix is the first 8 characters of the upload's `file_uuid`, so the path is predictable once the folder is known.
- The first upload was still present when the second arrived. Uploads accumulate in the session's folder.

The second message was sent after the session had been idle for about 10 minutes. The UI showed "Connecting" and the reply took about 5 minutes. It finished while the page still showed "running"; a page reload displayed the reply.

## Other things the capture showed

- **Session paths.** The web UI creates sessions with `POST /v1/code/sessions`, not `/v1/sessions`. The body is `{title, tags, environment_id, events, config: {append_system_prompt, model, effort_level, sources, outcomes}}`. Fyrd's design doc lists `session_context` where the UI sends `config`. Fyrd's client may be using an older alias.
- **Two id forms.** Events are posted to `/v1/code/sessions/cse_<id>/events` and streamed from `.../events/stream`. Status reads and title updates use `/v1/code/sessions/session_<id>`. The suffix after the prefix is the same.
- **Cold start.** The first container took about 3 minutes to set up before Claude replied.

## Clearing a session's context (`/clear`)

Tested on the same probe session, 2026-10-31.

- The web UI sends `/clear` as an ordinary user event, the same shape as any message:
  `{"events":[{"payload":{"type":"user","uuid":"…","session_id":"…","parent_tool_use_id":null,"message":{"role":"user","content":"/clear"}}}]}` to `POST /v1/code/sessions/cse_<id>/events`.
- The session id and URL stay the same. The transcript empties, and context usage dropped from 9% to the 6% baseline.
- Asked afterwards, from memory only, what had been asked earlier and which files were attached, Claude replied `NONE`.
- Uploaded files stay on disk under `/root/.claude/uploads/`; `/clear` clears the conversation, not the filesystem.

Not yet verified:

- Whether `append_system_prompt` set at creation survives `/clear`. The probe session was created from the UI, which sends no persona.
- Whether `/clear` runs when it arrives through the `claude-code-remote` connector's `send_message` (a turn "sent by a session") instead of from a client posting the user event directly. The cross-session messaging docs say such messages never run commands.

## The `claude-code-remote` tools

The probe session's MCP server list showed `claude-code-remote` among the claude.ai connectors. It reaches cloud sessions as a connector, not from the laptop's MCP config.

## Open questions

- Does the upload endpoint accept Fyrd's OAuth bearer token, or only browser cookies?
- Size and type limits on uploads.
- How long an uploaded file stays retrievable. Files did survive a ~10 minute idle pause (the folder was still there on resume); a full VM reclaim is untested.
