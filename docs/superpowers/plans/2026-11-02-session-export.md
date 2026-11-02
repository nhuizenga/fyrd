# Session raw-event export: implementation plan

**Date:** 2026-11-02
**Status:** Ready to build
**Request:** Add support for exporting a session's entire raw event data.

## Goal

From the console, the owner can download every saved event of a hub session (Controller or peasant) as one file of
raw event JSON, exactly as the Claude cloud-sessions API returned it, for debugging, auditing, or offline analysis.

## Decisions (settled with the owner)

| # | Decision | Choice |
|---|----------|--------|
| 1 | Entry point | A new server endpoint, `GET /api/hub/sessions/{id}/export`, plus a **Download** icon button in the console's session list, next to Rename and Archive. The endpoint also works with curl. No slash command. |
| 2 | Format | **JSONL**: one JSON object per line. Line 1 is a header (`"type":"fyrd_export"`) with session metadata, then one raw event per line in transcript order. |
| 3 | Source | The **paged REST list**, `GET /v1/sessions/{id}/events`, walked forward with `after_id` until `has_more` is false. This is the same call and pattern `HubRunner.FetchAfterAsync` already uses. It does not use the live stream, which never closes on its own. |
| 4 | Scope | **Hub sessions only, one at a time.** It uses the same `InHub` guard as the other session actions: only sessions the last poll listed can be exported, Controller included. There is no bulk export and no export of arbitrary session ids. |

One default the Builder should keep unless the owner says otherwise: the file ends with a **trailer line**
(`"type":"fyrd_export_end"`, with the event count). A file without the trailer was cut short (see Risks).

## File format

`<title-slug>-<session_id>-<yyyyMMdd-HHmmss>.jsonl`, UTF-8, `\n` line endings, no BOM, no indentation.

```jsonl
{"type":"fyrd_export","version":1,"exportedAt":"2026-11-02T20:10:00Z","sessionId":"session_…","title":"builder-x","role":"builder","session":{…raw GET /v1/sessions/{id} response…}}
{…raw event 1, exactly as in the API's data[]…}
{…raw event 2…}
…
{"type":"fyrd_export_end","eventCount":1234}
```

- `session` is `CloudSession.Raw`, fetched fresh with `ICloudSessions.GetAsync` at export time (not the cached poll
  view), so the file is self-describing.
- `role` comes from the hub's `HubSessionView` (null if it has none).
- Each event line is `SessionEvent.Raw` serialized with `JsonSerializer.Serialize(raw)`, which writes one line because
  default options don't indent. Fyrd adds no fields, removes none, and does not reorder.
- `version: 1` lets the format change later without breaking readers.
- The header and trailer use `"type"` values that no API event uses, so `jq 'select(.type=="assistant")'` and similar
  filters still work on the whole file.

## Approach

### Server (`runtime/`)

1. **A shared full-transcript pager.** `HubRunner.FetchAfterAsync` already does "page forward until
   `has_more` is false, and stop if `last_id` does not advance". Move that loop into an `IAsyncEnumerable` helper so the
   export streams page by page and never holds the whole transcript in memory:

   ```csharp
   // Fyrd.Core/CloudSessions/CloudSessionPaging.cs
   public static class CloudSessionPaging
   {
       /// Yields every saved event after `after` (null = from the first), oldest first, one page at a time.
       public static async IAsyncEnumerable<SessionEvent> AllEventsAsync(
           this ICloudSessions cloud, string sessionId, string? after = null, int pageLimit = 100,
           [EnumeratorCancellation] CancellationToken ct = default)
   }
   ```

   Change `HubRunner.FetchAfterAsync` to use it: `await foreach … events.Add(e)`, with the same behavior.
   `SpecRunner` (line 117) has its own loop. Leave it alone; it is out of scope.

2. **The endpoint**, in `HubEndpoints.MapHub`, next to `/sessions/{id}/stream`. Like `stream`, it writes to
   `HttpContext` directly and does not return an `IResult`:

   ```
   GET /api/hub/sessions/{id}/export
   ```

   Flow:
   1. If `id` is not in `state.Sessions`, return 404 `Session '{id}' is not a hub session.` (the same text `InHub` uses).
   2. **Before writing any bytes**, call `cloud.GetAsync(id)` and fetch the **first page** of events. These calls go
      through the same exception mapping as `Cloud(...)` (sign-in failure gives 502 with `SignedOut`, an API or HTTP
      error gives 502, a timeout gives 502, `HubConfigException` gives 400), so an early failure comes back as a normal
      problem response and the console can show it. The simplest way to do this: open the `AllEventsAsync` enumerator and
      call `MoveNextAsync()` once inside the guarded block, then keep enumerating after the headers are sent.
   3. Set the headers:
      - `Content-Type: application/x-ndjson; charset=utf-8`
      - `Content-Disposition: attachment; filename="<name>.jsonl"`. Build the name from a slug of the title
        (`[^a-z0-9]+`, replaced with `-`, trimmed, at most 60 characters, `session` if empty), the session id, and the
        UTC time. Also send `filename*=UTF-8''…` with the same value.
      - `Cache-Control: no-store`
   4. Write the header line, then each event line, then the trailer, flushing after each page (not after each line).
   5. **If a later page fails** (after the headers are sent), stop writing and call `http.Abort()`. The browser then
      marks the download as failed instead of saving a truncated file that looks complete. The missing trailer
      covers curl users. Log the failure with `ILogger`.
   6. If the client disconnects (`RequestAborted`), stop quietly, the same way `StreamTranscript` does.

   The endpoint is a GET, so `SameOriginMiddleware` lets it through. That is acceptable: no CORS headers are sent, so
   another origin can't read the response, and the endpoint changes nothing.

3. **Budget:** the export makes API reads only and starts no turns, so `BlockedError` does **not** apply. A blocked
   session can still be exported.

### Console (`console/`)

4. **`api.ts`:** add `export const exportUrl = (sessionId: string) => \`/api/hub/sessions/${enc(sessionId)}/export\`;`.
   This is a URL, not a `call<T>`, because the browser handles the download.

5. **`SessionsScreen.tsx`** (session row, around line 398): add a ghost/small icon `Button` before Rename:
   - icon `@servicetitan/anvil2/assets/icons/material/round/download.svg?react`. Check that the asset exists; if
     not, use `file_download.svg`.
   - `aria-label={\`Export ${s.title}\`}`, `title="Export raw events (JSONL)"`.
   - On click, fetch first so errors can be shown, then save:
     ```ts
     const res = await fetch(api.exportUrl(s.id));
     if (!res.ok) { show the problem `detail` the same way the archive and rename errors are shown; return; }
     const blob = await res.blob();
     // filename from Content-Disposition, fallback `${s.id}.jsonl`
     const a = document.createElement("a"); a.href = URL.createObjectURL(blob); a.download = name; a.click();
     URL.revokeObjectURL(a.href);
     ```
     A plain `<a href download>` would be simpler but would hide 404 and 502 errors behind a failed download. Using
     fetch then blob keeps errors visible in the console. The cost is that the whole file sits in the browser's
     memory, which is fine even for long sessions (tens of MB). While the request runs, disable the button and show
     a spinner or set `aria-busy`, because a long session takes several seconds to page through.
   - Show the button for every row, Controller included. The Controller can't be renamed, but it can be exported.

### Version

6. Bump `.claude-plugin/plugin.json` `version` **once** in the PR (0.2.17 → 0.2.18, or one patch past whatever
   `main` has at merge time), as `CLAUDE.md` requires.

## Files to change

| File | Change |
|------|--------|
| `runtime/src/Fyrd.Core/CloudSessions/CloudSessionPaging.cs` | **New.** `AllEventsAsync` extension |
| `runtime/src/Fyrd.Core/Hub/HubRunner.cs` | `FetchAfterAsync` uses `AllEventsAsync` |
| `runtime/src/Fyrd.Server/Endpoints/HubEndpoints.cs` | `GET /sessions/{id}/export` plus a private `ExportSession` writer and filename helper |
| `runtime/tests/Fyrd.Core.Tests/CloudSessionPagingTests.cs` | **New.** Pager tests |
| `runtime/tests/Fyrd.Server.Tests/HubApiTests.cs` (or new `HubExportTests.cs`) | Endpoint tests |
| `console/src/api.ts` | `exportUrl` |
| `console/src/screens/SessionsScreen.tsx` | Export button and download handler |
| `console/src/screens/SessionsScreen.test.tsx` | Button tests |
| `console/e2e/mockApi.ts`, `console/e2e/console.spec.ts` | Mock route and e2e download check |
| `README.md` | One line under Usage on exporting from the console |
| `.claude-plugin/plugin.json` | Version bump |

## Ordered steps

1. Branch from `main`. Add `CloudSessionPaging.AllEventsAsync` with tests against `FakeCloudSessions` (set `PageSize`
   small to force multiple pages): it yields every event in order across pages; it stops when `has_more` is false; it
   stops when `LastId` doesn't advance; it starts after `after`; and it stops on cancellation.
2. Change `HubRunner.FetchAfterAsync` to use it. Run `HubRunnerTests` to confirm nothing changed.
3. Add the endpoint. Tests (with `FyrdFactory` and its fake cloud):
   - A hub session with N events across several pages gives 200, `application/x-ndjson`, an `attachment` disposition
     ending in `.jsonl`, and N+2 lines. Line 1 is `fyrd_export` with `sessionId`. Lines 2…N+1 are byte-equal to each
     fake event's `Raw`, in order. The last line is `fyrd_export_end` with `eventCount: N`.
   - A session with zero events gives a header and a trailer only.
   - A non-hub id gives 404.
   - A first-call auth failure (`ThrowOnNextCall(new CloudSessionAuthException(401))`) gives 502 with the sign-in detail,
     and no body was started.
   - A first-call `CloudSessionApiException` gives 502.
   - A failure on a later page aborts the response: the client sees an exception or an incomplete body with no trailer.
   - A session blocked by budget can still be exported (200).
   - The filename slug: a title with spaces or Unicode gives a safe name, and an empty title gives `session-…`.
4. Console: add `exportUrl`, the button, and the handler. Vitest: the button is shown per row (Controller included);
   a click fetches the export URL; on 502 the error detail is shown; the button is disabled while the request runs.
5. E2E: add the mock route in `mockApi.ts`. In `console.spec.ts`, click Export and assert on `page.waitForEvent("download")`:
   the file name ends with `.jsonl`.
6. README line and version bump.
7. Run the full test suites (below). Then do a manual smoke test against a real session (see Testing).

## Testing

```
dotnet test runtime/Fyrd.sln
cd console && npm test && npx playwright test
node --test "scripts/*.test.mjs"
```

Manual smoke test (needs a signed-in `claude auth`): `/fyrd:up`, open Sessions, export a long-running session, then:

```
head -1 file.jsonl | jq .type                 # "fyrd_export"
tail -1 file.jsonl | jq .eventCount           # equals the line count minus 2
jq -r .type file.jsonl | sort | uniq -c       # user/assistant/result/system/control_* …
curl -sD- http://127.0.0.1:4870/api/hub/sessions/<id>/export -o /dev/null   # check the headers
```

Also check the first event's `created_at` against the session's start time on claude.ai, so you know the export
starts at the beginning of the transcript.

## Risks and open questions

- **Ordering of the paged list (verify in the smoke test).** `HubRunner` already relies on
  `/v1/sessions/{id}/events` with no cursor returning the **oldest** events first, then paging forward with
  `after_id`. The MCP `list_events` tool describes a call with no cursor as returning the newest events. If the REST
  route behaves that way for long sessions, the export would start partway through the transcript (and `HubRunner`
  has the same latent bug). The smoke test above checks this. If it fails, page backward with `before_id` from the
  newest event until `has_more` is false and write the pages in reverse (or buffer to a temp file), then raise the
  `HubRunner` problem separately.
- **The REST payload is not the stream envelope.** The owner chose the REST payloads, so the file has no
  `sequence_num`, `source`, `received_at` or delivery timestamps. If those are needed later, add a second source with
  `?source=stream`. `version` in the header leaves room for that.
- **Long sessions.** A session with 10k+ events is 100+ pages at 100 per page, which takes several seconds and many
  API calls. The server streams, so it uses little memory. The console uses fetch then blob, so the whole file is in
  browser memory, which is acceptable at this size. If the API accepts a higher `limit` (untested), raise
  `pageLimit` for export only.
- **Failure in the middle of an export.** It is handled by aborting the connection and by the trailer line. A reader
  should treat a file with no `fyrd_export_end` line as incomplete.
- **Sensitive content.** Raw events contain everything the session saw: tool output, file contents, and possibly
  secrets printed by commands. The file is saved only to the owner's own machine. Mention this in the README line.
  Fyrd does no redaction.
- **Archived sessions can't be exported.** This follows from decision 4: once a session is archived, the poll stops
  listing it, so its export is gone from the console. If the owner wants this, a later change could allow ids that
  carry Fyrd's hub tag even after archiving.
