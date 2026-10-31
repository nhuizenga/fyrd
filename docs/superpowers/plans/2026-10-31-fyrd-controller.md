# Fyrd Controller (hub) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the one-Controller hub. The service manages the Controller session, personas, shared/role memory, the session cap and pending questions. A new Anvil2 console (Inbox, Sessions, Settings) replaces the old tabs. Persona seeds and four account skills are written for the Controller.

**Architecture:** New `Fyrd.Core/Hub/*` classes sit beside the untouched spec pipeline:
- stores: personas, memory, controller state
- prompt building
- a poll pass (`HubRunner`), run by `HubRunnerService` like `SpecRunnerService`

New `/api/hub/*` endpoints feed a rewritten React console built on `@servicetitan/anvil2`. The Controller itself acts through the `claude-code-remote` connector. The service only creates and recreates it, posts memory updates to it, captures `fyrd-remember` blocks, and surfaces `AskUserQuestion` calls.

**Tech Stack:** .NET (existing `Fyrd.Core`/`Fyrd.Server`, xUnit), React 19 + Vite + Vitest, `@servicetitan/anvil2@3.17.0`.

**Spec:** `docs/superpowers/specs/2026-10-31-fyrd-controller-design.md`

## Global Constraints

- No Fyrd runtime data in any repo. Everything the service writes goes under `FyrdPaths.Root` (`~/.fyrd/data`).
- Do not change the pipeline's runtime:
  - `personas/lead.md`, `implementer.md`, `reviewer.md`, `validator.md`, `research-lead.md` and `researcher.md`
  - `LeadPrompt`, `SpecRunner`, `SpecLauncher`, `LeadRelay`, `InboxStore`, `DecisionMemory`, `OwnerProxy`
  - their endpoints and tests
- Tags:
  - `fyrd-hub` on every hub session
  - `fyrd-role-<personaId>` on each
  - `fyrd-controller` on the Controller only
- The Controller's title is `controller`. A peasant's title is `<role>-<suffix>`.
- Session cap: global, default **20**, valid range 1–100, stored in `FyrdConfig.SessionCap`.
- Models offered: `claude-opus-5-5`, `claude-sonnet-5-5`, `claude-haiku-5-5`, `claude-fable-5-1`.
- Permission mode for hub sessions: `auto`. Environment: `FyrdConfig.EnvironmentId`.
- Memory reaches only sessions created after it is written. The single exception is the Controller, which receives updates as user messages.
- Cost, budget and PR/check columns are **placeholders** ("—", disabled buttons) in this plan.
- Builder default output is a pushed branch, not a PR.
- The Slack Monitor never posts to Slack.
- The Lead role is not seeded and is not offered.
- Every persona prefers `AskUserQuestion` for decisions and actions.

## Review Focus

1. **The Controller was archived or deleted on claude.ai.** "Open Controller" must ignore archived sessions and create a fresh one, not open a dead link. Test: Task 5, `FindOrCreate_IgnoresArchived`.
2. **"Open Controller" is double-clicked, or two tabs click it.** Exactly one session is created. Test: Task 5, `FindOrCreate_Concurrent_CreatesOne`.
3. **A `fyrd-remember` block is seen again on a later poll, or is malformed** (empty text, unknown scope). It is appended exactly once, and a bad block is skipped, not stored. Tests: Task 3 `Parse_*`, Task 6 `Poll_RememberAppendedOnce`.
4. **A question was answered on claude.ai instead of in the console.** The Inbox card disappears on the next poll. Test: Task 6, `Poll_AnsweredElsewhere_RemovesQuestion`.
5. **The owner is signed out of Claude.** The hub runner stops calling the API, sets `SignedOut`, and the console shows the banner. Test: Task 6, `Poll_AuthError_SetsSignedOut`.

---

### Task 1: Spike: answering questions, artifact inputs, account skills (owner-run checkpoint)

The owner runs this; agents can't read Claude credentials. Tasks 6 and 7 consume its fixtures.

**Files:**
- Modify: `spikes/cloud-sessions/Program.cs` (add `q9`)
- Create: `docs/superpowers/spikes/2026-10-31-hub-spike.md`
- Create: `runtime/tests/Fyrd.Core.Tests/Fixtures/ask-user-pending.json`, `ask-user-answered.json`

- [ ] **Step 1: Add `q9` to the spike program.** Pattern: `Q8()`.
  - Create a session with first message: `Use the AskUserQuestion tool to ask me "Pick a fruit" with options Apple and Banana, then tell me what I picked.`
  - Wait for the session to become idle or blocked, and dump every raw event from that turn to `q9-events.jsonl`.
  - Then, under `--answer-via tool-result|control-response`, post an answer of `Banana`:
    - `tool-result`: a user event whose content is `[{"type":"tool_result","tool_use_id":<id>,"content":"Banana"}]`.
    - `control-response`: if the pending item is a `control_request` with subtype `can_use_tool`, send `{"type":"control_response","response":{"subtype":"success","request_id":<id>,"response":{"behavior":"allow","updatedInput":{...input, "answers":{"Pick a fruit":"Banana"}}}}}`.
  - Print the model's next reply, and archive the session.
- [ ] **Step 2: The owner runs** `dotnet run --project spikes/cloud-sessions -- q9 --answer-via tool-result`, then the same with `--answer-via control-response`. Expected: one run makes the model reply with "Banana".
- [ ] **Step 3: Copy the raw events into fixtures.** The pending `AskUserQuestion` event goes into `ask-user-pending.json`. The events after a successful answer go into `ask-user-answered.json`, as a JSON array of raw events.
- [ ] **Step 4: Paste this prompt into a claude.ai/code session in the Default environment and record the answers.**
  ```text
  1. List your available skills and say whether any named fyrd-* appear.
  2. Fetch https://claude.ai/artifact/LyjRt8594EXGq4V8UsQn5E with any tool you have and say which tool worked.
  ```
  Also have `q9 --skills-probe` create an API session with the same prompt, to check whether account skills load in `/v1/sessions` sessions.
- [ ] **Step 5: Write the results** in the spike doc, under three headings: answer route and shape, artifact read, account skills. If no answer route works, record "console answers unsupported". Task 7 then uses its fallback.
- [ ] **Step 6: Commit** the spike doc, `Program.cs` and the fixtures. Message: `spike: hub question answering, artifact inputs, account skills`.

---

### Task 2: Config, paths and the session cap

**Files:**
- Modify: `runtime/src/Fyrd.Core/FyrdPaths.cs`, `runtime/src/Fyrd.Core/Config/FyrdConfig.cs`
- Test: `runtime/tests/Fyrd.Core.Tests/ConfigStoreTests.cs`

**Interfaces:**
- Produces:
  - `FyrdPaths.HubDir` = `Root/hub`
  - `FyrdPaths.HubPersonasDir` = `Root/hub/personas`
  - `FyrdPaths.HubMemoryDir` = `Root/hub/memory`
  - all three are created by the constructor
  - `FyrdConfig.SessionCap` (int, default `20`), plus `FyrdConfig.MinSessionCap = 1` and `MaxSessionCap = 100`

- [ ] **Step 1: Write failing tests** in `ConfigStoreTests`:
  - `Load_Default_SessionCapIs20`: `Assert.Equal(20, new ConfigStore(home.Paths).Load().SessionCap)`
  - `Load_OutOfRangeSessionCap_Clamped`: file `{"sessionCap":500}` loads as 100
  - `Save_SessionCapZero_Throws`: `ArgumentOutOfRangeException`
  - `Paths_CreateHubDirs`: `Directory.Exists` holds for all three new dirs
- [ ] **Step 2: Run them to verify they fail.** Run `dotnet test runtime --filter ConfigStoreTests`. Expected: compile errors or FAIL.
- [ ] **Step 3: Implement.** Add `int SessionCap = 20` as the last positional parameter of `FyrdConfig`. Clamp it in `Load` and validate it in `Save`, the same way as `SquadCap`. Add the three path properties.
- [ ] **Step 4: Run** `dotnet test runtime`. Expected: all PASS, including the existing tests.
- [ ] **Step 5: Commit** with message `feat(hub): session cap config and hub paths`.

---

### Task 3: Hub personas and memory stores, and the `fyrd-remember` parser

**Files:**
- Create: `runtime/src/Fyrd.Core/Hub/HubPersonas.cs`, `HubMemory.cs`, `RememberBlock.cs`
- Create seeds: `personas/hub/controller.md`, `researcher.md`, `builder.md`, `reviewer.md`, `validator.md`, `monitor.md` (placeholder body `TODO(Task 10)` plus front matter. Task 10 writes the real text.)
- Test: `runtime/tests/Fyrd.Core.Tests/HubPersonasTests.cs`, `HubMemoryTests.cs`, `RememberBlockTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record HubPersona(string Id, string Name, string Model, string Definition, bool Locked);
  public sealed class HubPersonas(FyrdPaths paths, string seedDir)
  {
      IReadOnlyList<HubPersona> List();          // seeds first on first call; ordered controller first, then by Name
      HubPersona? Get(string id);
      HubPersona Save(HubPersona p);             // id = slug of Name when new; validates Model ∈ allowed list
      void Delete(string id);                    // throws InvalidOperationException for Locked
  }
  public sealed record MemoryEntry(string Id, string Text, string Source, DateTimeOffset At);
  public sealed class HubMemory(FyrdPaths paths)
  {
      const string Shared = "shared";
      IReadOnlyList<MemoryEntry> List(string scope);                  // newest first
      IReadOnlyDictionary<string, IReadOnlyList<MemoryEntry>> All();  // scope → entries
      MemoryEntry Add(string scope, string text, string source, DateTimeOffset at);
      void Update(string scope, string id, string text);
      void Delete(string scope, string id);
      long Version { get; }                                          // increments on every write
      string Render(string scope);                                   // "- text" lines, oldest first; "" when empty
  }
  public sealed record RememberDecl(string Scope, string Text);      // Scope: "shared" | "role"
  public static class RememberBlock { IReadOnlyList<RememberDecl> Parse(string assistantText); }
  ```
- Storage:
  - personas: `HubPersonasDir/<id>.json`
  - memory: `HubMemoryDir/<scope>.json` (a JSON array)
  - Scope is `shared` or a persona id, matching `^[a-z0-9-]{1,40}$`. Anything else throws `ArgumentException`.
  - Writes go through `AtomicFile.Write` under a lock.
  - *(Plan's call: the spec named `.md` files under `memory/` and `personas/`. JSON under `hub/` is used so the console can edit and delete single entries, and so hub data stays separate from the pipeline's `memory/` files.)*
- Seed format: front matter `---\nname: Builder\nmodel: claude-sonnet-5-5\nlocked: false\n---\n<definition>`. The id is the file stem. A seed is copied only when `<id>.json` does not exist. Controller seed: `locked: true`, `model: claude-sonnet-5-5`. Researcher seed: `claude-opus-5-5`. Others: `claude-sonnet-5-5`.

- [ ] **Step 1: Write failing tests.**
  - `HubPersonasTests`:
    - `List_SeedsOnce_ControllerFirst`: seed dir with `controller.md` and `builder.md`. Assert `List()[0].Id == "controller"`. Edit the builder through `Save`, call `List()` again, and assert the edit survives, so seeds didn't overwrite it.
    - `Delete_Locked_Throws`
    - `Save_UnknownModel_Throws`
    - `Save_New_IdIsSlug`: `"Data Engineer"` gives `data-engineer`.
  - `HubMemoryTests`:
    - `Add_ThenList_NewestFirst`
    - `Update_And_Delete_ById`
    - `Version_IncrementsOnWrite`
    - `Render_OldestFirstBullets`
    - `BadScope_Throws`: `"../x"`
  - `RememberBlockTests`:
    - `Parse_ScopeAndText`: a block with `scope: role\nAlways ask before X.` gives `("role","Always ask before X.")`.
    - `Parse_DefaultScopeIsRole`
    - `Parse_EmptyText_Skipped`
    - `Parse_UnknownScope_Skipped`
    - `Parse_TwoBlocks_Both`
    - `Parse_TextOver2000Chars_Skipped`
- [ ] **Step 2: Run** `dotnet test runtime --filter "HubPersonas|HubMemory|RememberBlock"`. Expected: FAIL.
- [ ] **Step 3: Implement the three classes and the six seed files.** `RememberBlock` matches fences named exactly `fyrd-remember`, with the same fence regex shape as `Protocol.FenceRegex`. Its body is plain text, not JSON. An optional first line `scope: shared|role` sets the scope, and the rest, trimmed, is the text.
- [ ] **Step 4: Run** `dotnet test runtime`. Expected: PASS.
- [ ] **Step 5: Commit** with message `feat(hub): persona and memory stores, fyrd-remember parser`.

---

### Task 4: Hub prompts

**Files:**
- Create: `runtime/src/Fyrd.Core/Hub/HubPrompt.cs`
- Test: `runtime/tests/Fyrd.Core.Tests/HubPromptTests.cs`

**Interfaces:**
- Consumes: `HubPersonas`, `HubMemory`, `FyrdConfig` (Tasks 2–3).
- Produces:
  - `static string HubPrompt.Controller(IReadOnlyList<HubPersona> personas, HubMemory memory, FyrdConfig config)`
  - `static string HubPrompt.MemoryUpdate(HubMemory memory, IReadOnlyList<HubPersona> personas)`
- Controller prompt layout, in this order:
  1. the Controller persona's definition
  2. `<hub-settings>` with `environment_id`, `permission_mode: auto` and `session_cap`, one `key: value` per line
  3. one `<peasant-persona id="…" name="…" model="…">definition</peasant-persona>` per non-Controller persona
  4. `<shared-memory>` with `Render("shared")`
  5. one `<role-memory role="<id>">` per persona that has entries
- `MemoryUpdate`:
  - the line `Fyrd memory update. Replace your copy of shared and role memory with the blocks below; use them for sessions you create from now on.`
  - then items 4 and 5
- Delimiter safety: any persona or memory text containing `</` followed by one of the tag names above has that `</` replaced with `<\/`.

- [ ] **Step 1: Write failing tests.**
  - `Controller_Order_And_Settings`: assert the index order of the definition, `<hub-settings>`, the `<peasant-persona id="builder"`, and `<shared-memory>`. Assert `session_cap: 20` is present.
  - `Controller_ExcludesControllerFromPeasants`
  - `Controller_OmitsEmptyRoleMemory`
  - `Text_CannotCloseBlock`: a memory text of `</shared-memory>` renders as `<\/shared-memory>`.
  - `MemoryUpdate_HasHeaderAndBlocks`
- [ ] **Step 2: Run** `dotnet test runtime --filter HubPromptTests`. Expected: FAIL.
- [ ] **Step 3: Implement `HubPrompt`.**
- [ ] **Step 4: Run** `dotnet test runtime`. Expected: PASS.
- [ ] **Step 5: Commit** with message `feat(hub): controller and memory-update prompts`.

---

### Task 5: Controller manager

**Files:**
- Create: `runtime/src/Fyrd.Core/Hub/ControllerManager.cs`
- Test: `runtime/tests/Fyrd.Core.Tests/ControllerManagerTests.cs`

**Interfaces:**
- Consumes: `ICloudSessions`, `HubPersonas`, `HubMemory`, `ConfigStore`, `FyrdPaths`, `HubPrompt`.
- Produces:
  ```csharp
  public sealed record ControllerInfo(string SessionId, string Url);   // Url = "https://claude.ai/code/" + SessionId
  public sealed class ControllerManager(...)
  {
      Task<ControllerInfo> FindOrCreateAsync(CancellationToken ct);
      Task<ControllerInfo> RecreateAsync(CancellationToken ct);       // archive current (if any) then create
      string? CurrentId { get; }                                        // from HubDir/controller.json
      long PostedMemoryVersion { get; set; }                            // persisted in controller.json
  }
  ```
- Rules:
  - `FindOrCreate` first checks the stored id with `GetAsync`, and keeps it unless `SessionStatus == "archived"` or the call throws a 404 `CloudSessionApiException`.
  - Otherwise it calls `ListByTagAsync("fyrd-controller")`, picks the newest non-archived session, and archives any others.
  - Otherwise it creates one:
    - title `controller`
    - tags `fyrd-hub`, `fyrd-role-controller`, `fyrd-controller`
    - model: the Controller persona's model
    - `PermissionMode = "auto"`, `EnvironmentId` from config
    - `AppendSystemPrompt = HubPrompt.Controller(...)`
    - `FirstMessage = "You are online. Wait for the owner's first request."`
  - It throws `InvalidOperationException("Set a cloud environment in Settings first.")` when `EnvironmentId` is null.
  - It runs under a `SemaphoreSlim(1)`.
  - On create, it sets `PostedMemoryVersion = memory.Version`.

- [ ] **Step 1: Write failing tests** with `FakeCloudSessions` and `TempHome`:
  - `FindOrCreate_None_CreatesWithTagsAndPrompt`: assert the tags, the `"auto"` mode, that `AppendSystemPrompt` contains `<hub-settings>`, and that the URL is `https://claude.ai/code/created-1`.
  - `FindOrCreate_StoredLive_Reuses`: `Created` stays empty.
  - `FindOrCreate_IgnoresArchived`: the stored session has status `archived`, so a new one is created.
  - `FindOrCreate_Concurrent_CreatesOne`: two `Task.WhenAll` calls, and `Assert.Single(cloud.Created)`.
  - `FindOrCreate_TwoTagged_ArchivesExtra`
  - `Recreate_ArchivesThenCreates`
  - `FindOrCreate_NoEnvironment_Throws`
- [ ] **Step 2: Run** `dotnet test runtime --filter ControllerManagerTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Extend `FakeCloudSessions.CreateAsync` to also register the new session under each of its tags in `_byTag`, so `ListByTagAsync` sees it.
- [ ] **Step 4: Run** `dotnet test runtime`. Expected: PASS.
- [ ] **Step 5: Commit** with message `feat(hub): controller find/create/recreate`.

---

### Task 6: Hub runner (poll pass)

**Files:**
- Create: `runtime/src/Fyrd.Core/Hub/HubRunner.cs`, `HubState.cs`, `PendingQuestions.cs`
- Create: `runtime/src/Fyrd.Server/HubRunnerService.cs`
- Modify: `runtime/src/Fyrd.Server/Program.cs` (register the stores and the service, skipped when `Fyrd:DisableRunner`)
- Test: `runtime/tests/Fyrd.Core.Tests/HubRunnerTests.cs`, `PendingQuestionsTests.cs`

**Interfaces:**
- Consumes: Task 1 fixtures, Tasks 3–5.
- Produces:
  ```csharp
  public sealed record HubQuestion(string Id, string SessionId, string SessionTitle, string Question,
      IReadOnlyList<string> Options, bool MultiSelect, DateTimeOffset AskedAt, string ToolUseId);
  public sealed record HubSessionView(string Id, string Title, string Role, string Status,
      string? Repo, string? Branch, DateTimeOffset? CreatedAt, DateTimeOffset? LastActivity, IReadOnlyList<string> Tags);
  public sealed class HubState
  {
      IReadOnlyList<HubSessionView> Sessions; IReadOnlyList<HubQuestion> Questions;
      bool SignedOut; string? LastError; DateTimeOffset? LastPollAt;
  }
  public static class PendingQuestions { IReadOnlyList<HubQuestion> Find(string sessionId, string title, IReadOnlyList<SessionEvent> events); }
  public sealed class HubRunner(ICloudSessions cloud, HubMemory memory, ControllerManager controller,
      HubState state, FyrdPaths paths, TimeProvider time) { Task PollOnceAsync(CancellationToken ct); }
  ```
- Each pass:
  1. `ListByTagAsync("fyrd-hub")`, skipping archived sessions.
  2. For each session, page `ListEventsAsync` after its cursor (cursors live in `HubDir/cursors.json`), and parse `RememberBlock` from assistant events:
     - `scope: shared` → `"shared"`
     - `scope: role` → the session's `fyrd-role-*` id
     - the Controller's `role` scope → `"shared"`
     - source = the session title
  3. `PendingQuestions.Find` over the session's recent events (at most the last 200):
     - a question is pending when its `AskUserQuestion` request (shape from the Task 1 fixture) has no later answer
     - one `HubQuestion` per question in the tool input
     - `Id` = `<sessionId>:<toolUseId>:<index>`
  4. Build `HubSessionView` from the session:
     - `Role` from the tag
     - `Repo` and `Branch` from `Raw.session_context.sources[0]` / outcomes when present, else null
  5. If `memory.Version != controller.PostedMemoryVersion` and a Controller exists:
     - `SendMessageAsync(controllerId, HubPrompt.MemoryUpdate(...))`
     - then set `PostedMemoryVersion`
- On `CloudSessionAuthException` or `ClaudeAuthMissingException`: `SignedOut = true`, and stop the pass. Other per-session errors go to `LastError`, and the pass continues.
- `HubRunnerService`: every 10 s, or every 60 s while signed out, like `SpecRunnerService`.

- [ ] **Step 1: Write failing tests.**
  - `PendingQuestionsTests`:
    - `Find_PendingFixture_OneQuestion`: load `Fixtures/ask-user-pending.json`. Assert the question text `Pick a fruit` and the options `["Apple","Banana"]`.
    - `Find_AnsweredFixture_None`
  - `HubRunnerTests`:
    - `Poll_RememberAppendedOnce`: an assistant event with a `fyrd-remember` role block on a session tagged `fyrd-role-builder`. Poll twice, then `Assert.Single(memory.List("builder"))`, with source equal to the session title.
    - `Poll_ControllerRoleScope_GoesToShared`
    - `Poll_AnsweredElsewhere_RemovesQuestion`: the pending fixture gives one question. Add the answered events, poll again, and get zero.
    - `Poll_MemoryChanged_PostsUpdateToControllerOnce`: after `memory.Add`, two polls give one `Sent` entry to the Controller id, starting with `Fyrd memory update.`
    - `Poll_AuthError_SetsSignedOut`
    - `Poll_ArchivedSessionsSkipped`
- [ ] **Step 2: Run** `dotnet test runtime --filter "HubRunner|PendingQuestions"`. Expected: FAIL.
- [ ] **Step 3: Implement**, and register in `Program.cs`:
  - `HubPersonas`, with `seedDir` from `Fyrd:HubPersonasSeedDir`, defaulting to the repo's `personas/hub`, resolved like `Fyrd:PersonasDir`
  - `HubMemory`, `HubState`, `ControllerManager` and `HubRunner` as singletons
  - `HubRunnerService` as a hosted service, unless `Fyrd:DisableRunner`
- [ ] **Step 4: Run** `dotnet test runtime`. Expected: PASS.
- [ ] **Step 5: Commit** with message `feat(hub): poll pass for memory capture, questions and session list`.

---

### Task 7: Hub API

**Files:**
- Create: `runtime/src/Fyrd.Server/Endpoints/HubEndpoints.cs`
- Modify: `runtime/src/Fyrd.Server/Program.cs` (`app.MapHub()`), `runtime/src/Fyrd.Core/CloudSessions/ICloudSessions.cs`, `CloudSessionClient.cs`, `runtime/tests/Fyrd.Core.Tests/FakeCloudSessions.cs`
- Test: `runtime/tests/Fyrd.Server.Tests/HubApiTests.cs`, `runtime/tests/Fyrd.Core.Tests/CloudSessionClientTests.cs`

**Interfaces:**
- Consumes: Tasks 2–6.
- Produces `ICloudSessions.AnswerQuestionAsync(string sessionId, string toolUseId, IReadOnlyDictionary<string,string> answers, CancellationToken ct)`. The route and body are copied from the Task 1 spike's working route. If Task 1 recorded "console answers unsupported", it throws `NotSupportedException`.
- Endpoints, all under `/api/hub`. JSON uses camelCase, and errors go through `ApiErrors.Run`/`RunAsync`.

| Method & path | Body → result |
|---|---|
| `GET /state` | `{ sessions, questions, signedOut, lastError, lastPollAt, controller: { sessionId, url } \| null }` |
| `POST /controller` | → `ControllerInfo` (FindOrCreate) |
| `POST /controller/recreate` | → `ControllerInfo` |
| `GET /sessions/{id}/messages` | → last 50 user/assistant events `{ role: "user"\|"agent", text, at }` |
| `POST /sessions/{id}/message` | `{ text }` (1–20 000 chars) → 204, via `SendMessageAsync` |
| `POST /sessions/{id}/pause` | → 204, via `InterruptAsync` |
| `POST /sessions/{id}/resume` | → 204, sends `Continue where you left off.` |
| `POST /questions/{id}/answer` | `{ choice?: string, text?: string }` → 204, or 501 with `detail` "Answer this question on claude.ai." when unsupported |
| `GET/PUT/DELETE /personas[/{id}]` | `HubPersona`. DELETE on a locked persona → 409 |
| `GET /memory` | `{ scopes: [{ scope, label, entries }] }`, shared first |
| `POST /memory/{scope}` | `{ text }` → `MemoryEntry` (source `"owner"`) |
| `PUT/DELETE /memory/{scope}/{entryId}` | `{ text }` / – → 204 |

- `{id}` path values for sessions must be present in `HubState.Sessions`. Otherwise → 404. That way the console can't drive non-hub sessions.
- `/api/config` already handles `sessionCap` through `FyrdConfig`. No change is needed beyond Task 2.

- [ ] **Step 1: Write failing tests** with `FyrdFactory`:
  - `State_Empty_Ok`
  - `Controller_Post_CreatesOnce`: two POSTs, and `Assert.Single(f.Cloud.Created)`.
  - `Message_UnknownSession_404`
  - `Message_HubSession_SendsText`: seed `HubState.Sessions` from the service provider.
  - `Persona_DeleteController_409`
  - `Memory_AddEditDelete_RoundTrip`
  - `Answer_Supported_CallsCloud` (or `Answer_Unsupported_501`, whichever matches the Task 1 result)
  - `Config_SessionCap_RoundTrip`
  - In `CloudSessionClientTests`: `AnswerQuestion_PostsSpikeShape`, which asserts the request path and body against the Task 1 shape.
- [ ] **Step 2: Run** `dotnet test runtime --filter "HubApiTests|AnswerQuestion"`. Expected: FAIL.
- [ ] **Step 3: Implement** the endpoints, the client method and the fake (record answers in `Answered`).
- [ ] **Step 4: Run** `dotnet test runtime`. Expected: PASS.
- [ ] **Step 5: Commit** with message `feat(hub): hub API`.

---

### Task 8: Console shell on Anvil2, and removing the old tabs

**Files:**
- Modify: `console/package.json` (add `@servicetitan/anvil2@3.17.0`), `console/src/main.tsx` (import the Anvil2 stylesheet and provider per its README), `console/src/App.tsx`, `console/src/App.css`, `console/src/api.ts`, `console/src/types.ts`, `console/src/App.test.tsx`, `console/src/test/fixtures.ts`
- Delete: `console/src/board/*`, `console/src/inbox/*`, `console/src/memory/*`, `console/src/specs/*` and their tests (owner's decision: these tabs are replaced)
- Create: `docs/superpowers/mockups/2026-10-31-console/{Inbox,Sessions,Settings}.dc.html`, copied from the owner's canvas (`https://claude.ai/artifact/LyjRt8594EXGq4V8UsQn5E`, read with the Artifact tool), as the visual reference for Tasks 8–9

**Interfaces:**
- Produces:
  - `api.ts`: `getHubState`, `openController`, `recreateController`, `getMessages`, `sendMessage`, `pause`, `resume`, `answerQuestion`, `getPersonas`, `savePersona`, `deletePersona`, `getHubMemory`, `addMemory`, `editMemory`, `deleteMemory`. Keep `getConfig` and `putConfig`.
  - `types.ts`: `HubState`, `HubSessionView`, `HubQuestion`, `HubPersona`, `MemoryEntry`, `MemoryScope`, and `FyrdConfig` with `sessionCap`. Shapes come from Task 7.
  - `App`: top nav with `Inbox` (a count badge of `questions.length`), `Sessions` and `Settings`. It polls `getHubState` every 3000 ms while visible, using the same visibility logic as today, and passes `state` down. It keeps the signed-out banner and the "Can't reach the Fyrd server" error.
- Use Anvil2 components for every control. Find names with the `mcp__claude_ai_anvil-labs_2__list_components` / `get_component_guidance` tools. Delete the old hand-rolled CSS variables, and keep only layout CSS.

- [ ] **Step 1: Write failing `App.test.tsx` tests** (mock `../api`):
  - `renders three nav items with inbox count`: two questions → a nav button with the name `Inbox 2`.
  - `shows signed-out banner`
  - `shows server error when state fails`
- [ ] **Step 2: Run** `npm --prefix console test`. Expected: FAIL.
- [ ] **Step 3: Implement the shell.** Install the package, delete the old folders and their tests, and add stub `InboxScreen`, `SessionsScreen` and `SettingsScreen` components in `console/src/screens/`, each rendering its heading.
- [ ] **Step 4: Run** `npm --prefix console test` and `npm --prefix console run build`. Expected: PASS, and the build writes `runtime/src/Fyrd.Server/wwwroot`.
- [ ] **Step 5: Commit** with message `feat(console): Anvil2 shell with Inbox, Sessions, Settings`.

---

### Task 9: Console screens

**Files:**
- Create: `console/src/screens/InboxScreen.tsx`, `SessionsScreen.tsx`, `SettingsScreen.tsx`, each with a `.test.tsx`

**Interfaces:**
- Consumes: Task 8's `api.ts` and types, and `state: HubState` as a prop.
- The layout follows the mockups in `docs/superpowers/mockups/2026-10-31-console/`.

- [ ] **Step 1: Write failing tests:**
  - **Inbox:**
    - `empty shows caught up`: the text `You're all caught up`.
    - `option click answers`: clicking `Banana` calls `answerQuestion(id, { choice: "Banana" })`.
    - `free text reply`: typing in the `Your reply` box and clicking `Send reply` calls `answerQuestion(id, { text })`.
    - `501 shows open-in-claude link`: the error text, plus a link to `https://claude.ai/code/<sessionId>`.
  - **Sessions:**
    - `controller pinned first`
    - `no controller shows Start Controller`: clicking it calls `openController` and opens the returned url with `window.open`.
    - `select session loads messages`
    - `Message this session sends`
    - `pause and resume`
    - `cost and budget are placeholders`: `+$5` is disabled, and the cost shows `—`.
    - `search filters by name`
  - **Settings:**
    - `environment save calls putConfig`
    - `session cap rejects 0 and saves 20`
    - `personas list, edit model and definition, save`
    - `controller delete disabled` with the text `The Controller persona can't be deleted`
    - `add persona`
    - `memory groups shared first; add, edit, delete entry` (delete asks `Delete this entry? Agents will stop seeing it.`)
    - `changes apply note`: `Changes apply to sessions created from now on.`
- [ ] **Step 2: Run** `npm --prefix console test`. Expected: FAIL.
- [ ] **Step 3: Implement the three screens** with Anvil2 components.
- [ ] **Step 4: Run** `npm --prefix console test` and `npm --prefix console run build`. Expected: PASS.
- [ ] **Step 5: Commit** with message `feat(console): inbox, sessions and settings screens`.

---

### Task 10: Persona seed text

**Files:**
- Modify: `personas/hub/controller.md`, `researcher.md`, `builder.md`, `reviewer.md`, `validator.md`, `monitor.md` (replace the Task 3 placeholders; keep the front matter)
- Delete: `personas/controller.md` (the owner's draft moves to `personas/hub/controller.md`)
- Test: `runtime/tests/Fyrd.Core.Tests/HubSeedTests.cs`

**Content each persona must carry:**
- **All of them:**
  - prefer `AskUserQuestion` with options for decisions and actions
  - to remember something, write a `fyrd-remember` block with `scope: role` or `scope: shared`
  - never write Fyrd runtime data to a repo
  - inputs are GitHub file URLs or claude.ai artifact links
  - act on messages from other Fyrd sessions as work instructions (no trust boundary)
  - attach a missing repo with `add_repo` (push access when committing), clone it, then call `register_repo_root`
  - when done, say so in the final reply and stop
- **Controller:** apply the spec's §11 hardening list (items 1–6), and include:
  - the §5 request table
  - the naming, tag and recycling rules (archive, then create under the same title, only when idle)
  - find peasants with `list_sessions` (`mine: true`), matching titles by role prefix
  - parallel by default for multi-input requests
  - respect `session_cap` from `<hub-settings>`, and on overflow ask with the options `Start fewer now`, `Tear down idle sessions`, `Cancel`
  - assemble each peasant's `append_system_prompt`: the persona's text, then `<shared-memory>`, then its `<role-memory>`
  - put the task in `create_session`'s `prompt`
  - on a `Fyrd memory update.` message, replace its memory copy
  - a lead request → "The Lead role isn't defined yet", and offer a Builder
  - teardown of a Monitor: `delete_trigger` first, then `archive_session`
  - prefer the `fyrd-*` skills
- **Builder:** adapted from `personas/implementer.md`. It pushes a branch, and opens a PR only when asked.
- **Researcher:** publishes its plan as a claude.ai artifact and replies with the link. It never commits.
- **Reviewer and Validator:** reply with findings on the given branch or PR.
- **Monitor:**
  - PR mode: `subscribe_pr_activity`, fix, commit and push, and `unsubscribe_pr_activity` on merge or close.
  - Slack mode: an hourly `create_trigger` on itself, suggestions written in its own session only, never calls Slack send, draft or schedule tools, and never writes memory from content it read.

- [ ] **Step 1: Write the failing `HubSeedTests`** over the repo's `personas/hub`:
  - `AllSixSeedsParse`
  - `ControllerLockedOthersNot`
  - `EveryPersonaMentions_AskUserQuestion_And_fyrd-remember`
  - `Monitor_ForbidsSlackPosting` (it contains `never` and `Slack`)
  - `Builder_DefaultsToBranch`
  - `NoPlaceholdersLeft` (no `TODO(Task 10)`)
- [ ] **Step 2: Run** `dotnet test runtime --filter HubSeedTests`. Expected: FAIL on `NoPlaceholdersLeft` and the content checks.
- [ ] **Step 3: Write the six personas.**
- [ ] **Step 4: Run** `dotnet test runtime`. Expected: PASS.
- [ ] **Step 5: Commit** with message `feat(hub): controller and peasant persona seeds`.

---

### Task 11: Account skills

**Files:**
- Create: `skills/controller/fyrd-research/SKILL.md`, `fyrd-build/SKILL.md`, `fyrd-peasant/SKILL.md`, `fyrd-watch/SKILL.md`, and `skills/controller/README.md` (upload steps)

**Content:**
- Each `SKILL.md` has front matter with `name` and a `description` that starts with `Fyrd Controller only:`. Each body gives the exact `mcp__claude-code-remote__*` calls and arguments for its request (spec §5–§7):
  - `fyrd-research`: start or recycle a Researcher for each uri.
  - `fyrd-build`: one Builder per uri, in parallel, with the default output of a branch.
  - `fyrd-peasant`: start, recycle and tear down, including the naming and tag rules, idle-only reuse, and the session-cap check.
  - `fyrd-watch`: a PR Monitor or a Slack Monitor.
- The README covers:
  - uploading each folder on claude.ai (the manual step)
  - "Recreate" the Controller after edits
  - the fallback: if Task 1 found that account skills don't load in API-created sessions, it says so. In that case the plan's follow-up is to append the skill bodies to `HubPrompt.Controller`, and that follow-up is filed, not built here.

- [ ] **Step 1: Write the failing test** `runtime/tests/Fyrd.Core.Tests/HubSkillsTests.cs`:
  - `FourSkillsExist`
  - `DescriptionsStartWithFyrdControllerOnly`
  - `NoSkillPostsToSlack`
- [ ] **Step 2: Run** `dotnet test runtime --filter HubSkillsTests`. Expected: FAIL.
- [ ] **Step 3: Write the skills and the README.**
- [ ] **Step 4: Run** `dotnet test runtime`. Expected: PASS.
- [ ] **Step 5: Commit** with message `feat(hub): controller account skills`.

---

### Task 12: Owner smoke run (owner-run checkpoint)

**Files:**
- Create: `docs/superpowers/spikes/2026-10-31-hub-smoke.md`

- [ ] **Step 1:** The owner uploads the four skills (Task 11 README), starts Fyrd, sets the environment in Settings, and clicks **Start Controller**. Expected: one `controller` session opens on claude.ai.
- [ ] **Step 2:** The owner sends the Controller `Build the following: <two GitHub file URLs>`. Expected:
  - two `builder-<name>` sessions appear in Sessions, tagged `fyrd-hub` and `fyrd-role-builder`
  - each pushes a branch
- [ ] **Step 3:** The owner tells one Builder `Remember: always ask before deleting files.` Expected: within about 10 s, a Builder memory entry appears in Settings, with that session as its source.
- [ ] **Step 4:** The owner asks the Controller to tear down one Builder. Then the owner causes a question: tell a Builder to ask before an action. Expected:
  - the torn-down session is archived
  - an Inbox card appears with buttons, and clicking one answers it (or the card shows the claude.ai link if Task 1 found answering unsupported)
- [ ] **Step 5:** Record the results, then commit with message `docs: hub smoke run`.
