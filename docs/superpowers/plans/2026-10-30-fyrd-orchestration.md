# Fyrd Orchestration (Plan 2 of 4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Attacking a spec in the console launches a lead cloud session that breaks the spec into units and runs implementer → reviewer → validator peer sessions for them. Fyrd watches every session, so the Board shows live unit stages, the lead's questions arrive as Inbox cards, and the owner's answers go back to the lead.

**Architecture:**
- **Launch:** `POST /api/specs/{id}/attack` claims the spec (Draft → Queued, atomically). `SpecLauncher` then creates the lead through `CloudSessionClient`, with permission mode `auto`, a prompt built from persona files, the spec, remembered decisions, and the squad cap.
- **Watch:** a background `SpecRunner` polls every 10 s. It reads new lead events, parses fenced `fyrd-*` protocol blocks, and finds peer sessions by tag. From those it updates `units.json`, the spec's status, and the Inbox.
- **Relay:** answering a card or changing the squad cap sends a `fyrd-answer` or `fyrd-config` block to the lead.
- **Server-side coordination:** all coordination between peers stays in the cloud, with the lead using its `claude-code-remote` tools. Fyrd only launches, observes, and relays.

**Tech Stack:** unchanged from Plan 1: .NET 10, ASP.NET Core minimal APIs, `HttpClient`, `BackgroundService`, xUnit, `Microsoft.AspNetCore.Mvc.Testing`; React + TypeScript + Vite, Vitest; Node launcher.

**Spec:** `docs/superpowers/specs/2026-10-29-fyrd-design.md`. Also read `docs/superpowers/spikes/2026-10-29-cloud-sessions.md` (verified API shapes and findings).

**Out of scope (later plans):** research stage, owner-proxy persona, and memory beyond injection (Plan 3); integration-branch handoff card and PR (Plan 4). In this plan, a spec ends at `ReadyForHandoff` with the integration branch named on the Board.

## Global Constraints

- Peons are Claude Code cloud sessions driven only through `CloudSessionClient`. No other code calls `api.anthropic.com`.
- The claude.ai OAuth token is read per request, and is never logged, stored elsewhere, or sent anywhere but `https://api.anthropic.com`.
- **Every lead is created with permission mode `auto`** (owner's decision), using this create event ahead of the first user message: `{"type":"event","data":{"type":"control_request","request_id":"set-mode-<guid>","request":{"subtype":"set_permission_mode","mode":"auto"}}}`.
- **Session tags:** `fyrd`, `fyrd-spec-<specId>`, `fyrd-role-<lead|implementer|reviewer|validator>`, and `fyrd-unit-<unitId>` where it applies.
- **Branches:** unit `fyrd/<specId>/unit/<unitId>`; integration `fyrd/<specId>/integration` (never a bare `fyrd/<specId>`: git cannot hold a branch and a branch folder of the same name). Peers never force-push and never touch other branches. Fyrd never merges a PR.
- **Default models:** lead `claude-opus-5-5`, peers `claude-sonnet-5-5`. Both are configurable.
- Squad cap is 1–8, default 4. The lead always receives the current cap.
- All state stays hand-editable under `~/.fyrd/data/`; a malformed file must never take down the console.
- **Verified endpoints** (from the spike findings):
  - list: `GET /v1/sessions?tags=<tag>&limit=100` (paged by `has_more` / `after_id`)
  - interrupt: `POST /v1/sessions/{id}/events` with `{"events":[{"type":"control_request","request_id":"<uuid>","request":{"subtype":"interrupt","cancel_queued":true}}]}`
  - archive: `POST /v1/code/sessions/{id}/archive` with body `{}`
- Minimal dependencies: no new packages.

## Protocol (exact)

The lead talks to Fyrd only through fenced blocks in its assistant messages. Each block is a code fence whose info string is the block name and whose body is one JSON value. Fyrd sends blocks back as user messages.

| Block | Direction | Body |
|---|---|---|
| `fyrd-units` | lead → Fyrd | `[{"id":"<unitId>","title":"<text>","done":false}]` (full list each time; `id` matches `^[a-z0-9-]{1,40}$`) |
| `fyrd-question` | lead → Fyrd | `{"id":"<questionId>","question":"…","options":["…"],"recommendation":"…","reason":"…","unit":"<unitId or null>","details":"…"}` |
| `fyrd-done` | lead → Fyrd | `{"branch":"fyrd/<specId>/integration","summary":"…"}` |
| `fyrd-failed` | lead → Fyrd | `{"reason":"…"}` |
| `fyrd-answer` | Fyrd → lead | `{"id":"<questionId>","answer":"…"}` |
| `fyrd-config` | Fyrd → lead | `{"squadCap":<n>}` |

## Decisions made in this plan (for owner review)

- **Environment ID** is a config value (`environmentId`) that the owner pastes into Settings on the Board. Attacking without one returns 409 "Set the cloud environment in Settings". There is no environment-listing call, because that endpoint is unverified.
- **Personas** are Markdown files in the plugin's `personas/` folder. The server finds them through config key `Fyrd:PersonasDir`, which the launcher passes.
- **Permission-prompt detection:** when the lead is `idle`, `post_turn_summary.needs_action` is non-empty, and the latest assistant turn has no `fyrd-question`, Fyrd makes a card "Lead needs attention: <needs_action>". It carries the session link and allows free text, and the answer is sent to the lead as a plain message.
- **Unit stage** comes from the unit's newest peer session role: implementer → `Implement`, reviewer → `Review`, validator → `Validate`. The unit's `done` flag in `fyrd-units` → `Done`.
- **Unit state** comes from that session: `idle` → `Idle`, otherwise `Running`. An open card for the unit → `WaitingOnOwner`. `Done` when done.
- **If the launch fails** (API error), the spec goes back to `Draft` and the endpoint returns 502 with the reason.

## Review Focus

1. **The lead's message has a malformed `fyrd-*` block** (bad JSON, wrong shape). Expected: the block is skipped with a warning; polling continues; other blocks in the same message still apply. Test: Task 2 `Parse_SkipsMalformedBlock_KeepsOthers`.
2. **The same `fyrd-question` is seen on two polls** (overlapping pages, server restart). Expected: exactly one Inbox card. Test: Task 6 `Poll_SameQuestionTwice_OneCard`.
3. **Fyrd restarts mid-run** (owner runs `/fyrd:down` then `/fyrd:up`). Expected: polling resumes from the stored event cursor, with no duplicate cards and no re-launch. Test: Task 6 `Poll_ResumesFromStoredCursor`.
4. **Two attacks on one spec at once** (double click, two tabs). Expected: exactly one lead session is created, and the second request gets 409. Test: Task 5 `Attack_Concurrent_CreatesOneLead`.
5. **The token expires or the owner is signed out mid-run.** Expected: polling backs off, the Board shows "Signed out of Claude — run `claude auth login`", nothing crashes, and polling recovers when credentials return. Test: Task 6 `Poll_AuthFailure_SetsSignedOut_ThenRecovers`.

---

## File Structure

```
personas/                                   # plugin-shipped role personas (Markdown)
  lead.md  implementer.md  reviewer.md  validator.md
runtime/src/Fyrd.Core/
  CloudSessions/ICloudSessions.cs           # interface CloudSessionClient implements (for fakes)
  CloudSessions/CloudSessionClient.cs       # + ListByTagAsync, InterruptAsync, ArchiveAsync, permission mode, hardening
  CloudSessions/CloudSessionModels.cs       # + PermissionMode on CreateSessionRequest, SessionListPage
  CloudSessions/ClaudeCredentials.cs        # + org-uuid cache keyed by ~/.claude.json mtime
  Orchestration/Protocol.cs                 # parse fyrd-* blocks; format fyrd-answer / fyrd-config
  Orchestration/LeadPrompt.cs               # builds the lead's first message from personas + spec + memory + config
  Orchestration/SpecLauncher.cs             # attack → create lead
  Orchestration/SpecRunner.cs               # one poll pass over all active specs (pure, testable)
  Orchestration/RunnerState.cs              # auth state + last error, read by /api/board
  Config/FyrdConfig.cs                      # + EnvironmentId, LeadModel, PeerModel
  Specs/SpecModels.cs, SpecStore.cs         # + LastEventId, Branch, TryTransition, SetLead, SetCursor, WriteUnits
  Inbox/InboxModels.cs, InboxStore.cs       # + SourceKey, HasOpenOrAnswered(sourceKey)
runtime/src/Fyrd.Server/
  SpecRunnerService.cs                      # BackgroundService: calls SpecRunner.PollOnceAsync every 10 s
  Endpoints/BoardEndpoints.cs               # GET /api/board aggregate
  Endpoints/SpecEndpoints.cs, InboxEndpoints.cs, ConfigEndpoints.cs, Program.cs  # modified
scripts/fyrd.mjs                            # + --Fyrd:PersonasDir
console/src/...                             # Board uses /api/board; Settings; session links; banners
```

---

### Task 1: CloudSessionClient additions

**Files:**
- Create: `runtime/src/Fyrd.Core/CloudSessions/ICloudSessions.cs`
- Modify: `CloudSessions/CloudSessionClient.cs`, `CloudSessions/CloudSessionModels.cs`, `CloudSessions/ClaudeCredentials.cs`
- Test: `runtime/tests/Fyrd.Core.Tests/CloudSessionClientTests.cs`, `ClaudeCredentialsTests.cs`

**Interfaces:**
- Consumes: the Plan 1 client (`CreateAsync`, `GetAsync`, `ListEventsAsync`, `SendMessageAsync`, `FakeHandler` test helper).
- Produces:
  - `public interface ICloudSessions`, implemented by `CloudSessionClient`, with:
    - `CreateAsync(CreateSessionRequest, CancellationToken)`
    - `GetAsync(string, CancellationToken)`
    - `ListEventsAsync(string, int, string?, string?, CancellationToken)`
    - `SendMessageAsync(string, string, CancellationToken)`
    - `Task<IReadOnlyList<CloudSession>> ListByTagAsync(string tag, CancellationToken ct = default)`: follows `has_more` with `after_id`, max 20 pages.
    - `Task InterruptAsync(string sessionId, CancellationToken ct = default)`
    - `Task ArchiveAsync(string sessionId, CancellationToken ct = default)`
  - `CreateSessionRequest` gains a trailing `string? PermissionMode = null`. When it is set, the create body's `events` array starts with the `set_permission_mode` control event (Global Constraints), then the user message event.
  - A 2xx response that isn't JSON throws `CloudSessionApiException(status, "<non-JSON body>")`.
  - `ClaudeCredentials` caches `organizationUuid` until `~/.claude.json`'s last-write time changes. It still re-reads `.credentials.json` on every call.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task Create_WithPermissionMode_PrependsSetModeEvent()
{ /* body.events[0].data.type == "control_request";
     body.events[0].data.request.subtype == "set_permission_mode";
     body.events[0].data.request.mode == "auto";
     body.events[0].data.request_id starts with "set-mode-";
     body.events[1].data.type == "user" */ }
[Fact] public async Task Create_WithoutPermissionMode_HasOnlyUserEvent() { }
[Fact] public async Task ListByTag_SendsTagsQuery_AndFollowsPages()
{ /* first response has_more=true,last_id="s2" → second request has after_id=s2; returns 3 sessions total;
     every request path starts "/v1/sessions?tags=fyrd-spec-x" */ }
[Fact] public async Task Interrupt_PostsControlRequestEvent()
{ /* POST /v1/sessions/session_1/events; events[0].type=="control_request"; request.subtype=="interrupt"; request.cancel_queued==true */ }
[Fact] public async Task Archive_PostsToCodeSessionsPath() { /* POST /v1/code/sessions/session_1/archive, body "{}" */ }
[Fact] public async Task Get_NonJson2xx_ThrowsApiException() { /* 200 with "<html>" */ }
[Fact] public void Read_CachesOrgUuidUntilClaudeJsonChanges()
{ /* read twice → same; rewrite file with new uuid + newer mtime → new uuid */ }
```

- [ ] **Step 2: Run them; expect compile errors or failures.** Run: `dotnet test runtime/Fyrd.sln --filter "CloudSessionClientTests|ClaudeCredentialsTests"`
- [ ] **Step 3: Implement.** Keep all request shapes in `CloudSessionClient` (design §11: one class knows the API).
- [ ] **Step 4: Run the full suite; expect all to pass.** Run: `dotnet test runtime/Fyrd.sln`
- [ ] **Step 5: Commit** — `feat(core): list/interrupt/archive, permission mode, client hardening`

---

### Task 2: Protocol parser

**Files:**
- Create: `runtime/src/Fyrd.Core/Orchestration/Protocol.cs`
- Test: `runtime/tests/Fyrd.Core.Tests/ProtocolTests.cs`

**Interfaces:**
- Produces (namespace `Fyrd.Core.Orchestration`):
  - `sealed record UnitDecl(string Id, string Title, bool Done)`
  - `sealed record QuestionDecl(string Id, string Question, IReadOnlyList<string> Options, string? Recommendation, string? Reason, string? Unit, string? Details)`
  - `sealed record DoneDecl(string Branch, string? Summary)`
  - `sealed record FailedDecl(string Reason)`
  - `sealed record ParsedBlocks(IReadOnlyList<UnitDecl>? Units, IReadOnlyList<QuestionDecl> Questions, DoneDecl? Done, FailedDecl? Failed, IReadOnlyList<string> Warnings)`
  - `static class Protocol`:
    - `ParsedBlocks Parse(string assistantText)`: finds fenced blocks with info strings `fyrd-units`, `fyrd-question`, `fyrd-done`, `fyrd-failed`. Fences are 3+ backticks and may be indented. When a message has several `fyrd-units` blocks, the last one wins.
    - `string FormatAnswer(string questionId, string answer)` → a fenced `fyrd-answer` block.
    - `string FormatConfig(int squadCap)` → a fenced `fyrd-config` block.
  - Validation: a unit `id` must match `^[a-z0-9-]{1,40}$`, and a question needs a non-empty `id` and `question`. Anything invalid is dropped and a warning is added.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Parse_QuestionBlock_FromSpikeShape()
{ /* input: "I ran a tool…\n\n```fyrd-question\n{\"id\":\"q-color\",\"question\":\"Which colour?\",\"options\":[\"red\",\"blue\"]}\n```"
     → Questions[0].Id == "q-color", Options == ["red","blue"] */ }
[Fact] public void Parse_UnitsBlock_LastOneWins() { }
[Fact] public void Parse_DoneAndFailed() { }
[Fact] public void Parse_SkipsMalformedBlock_KeepsOthers()
{ /* one fyrd-question with "{nope", one valid fyrd-units → Units.Count==1, Questions empty, Warnings.Count==1 */ }
[Fact] public void Parse_RejectsBadUnitId() { /* "../x" dropped with warning */ }
[Fact] public void Parse_IgnoresOtherFences() { /* ```json …``` → nothing */ }
[Fact] public void FormatAnswer_RoundTripsThroughJson() { /* answer with quotes/newlines survives */ }
```

- [ ] **Step 2: Run them; expect failures.** Run: `dotnet test runtime/Fyrd.sln --filter ProtocolTests`
- [ ] **Step 3: Implement** with `System.Text.Json` and one compiled regex for fences.
- [ ] **Step 4: Run them; expect all to pass.**
- [ ] **Step 5: Commit** — `feat(core): fyrd protocol block parser`

---

### Task 3: Store and config extensions

**Files:**
- Modify: `runtime/src/Fyrd.Core/Config/FyrdConfig.cs`, `Specs/SpecModels.cs`, `Specs/SpecStore.cs`, `Inbox/InboxModels.cs`, `Inbox/InboxStore.cs`
- Test: `ConfigStoreTests.cs`, `SpecStoreTests.cs`, `InboxStoreTests.cs`

**Interfaces:**
- Produces:
  - `FyrdConfig(int SquadCap = 4, string? EnvironmentId = null, string LeadModel = "claude-opus-5-5", string PeerModel = "claude-sonnet-5-5")`.
    - `Load` keeps Plan 1's tolerance: malformed files fall back to defaults and squadCap is clamped.
    - A missing or empty model falls back to the default.
    - `EnvironmentId` must match `^(env|ccpool)_[A-Za-z0-9]+$` or be null. `Save` throws `ArgumentException` otherwise.
  - `SpecRun` gains trailing `string? LastEventId = null, string? Branch = null, string? Error = null`.
  - `SpecStore` additions:
    - `bool TryTransition(string id, SpecStatus from, SpecStatus to)`: atomic under the store's lock, and false if the current status is not `from`.
    - `SpecRun SetLead(string id, string sessionId)` (also sets status `Building`)
    - `SpecRun SetCursor(string id, string lastEventId)`
    - `SpecRun Finish(string id, SpecStatus status, string? branch, string? error)`
    - `void WriteUnits(string id, IReadOnlyList<Unit> units)`
    - `IReadOnlyList<SpecRun> ListActive()`: specs in `Queued`, `Building` or `FinalReview` with a valid run.json.
  - `InboxCard` gains trailing `string? SourceKey = null`.
  - `InboxStore.Add` gains trailing `string? sourceKey = null`.
  - `InboxStore.HasSourceKey(string sourceKey)` is true for open or answered cards with that key.
  - `InboxStore.ListOpenFor(string specId)`

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Config_DefaultsModelsAndNullEnvironment() { }
[Fact] public void Config_Save_RejectsBadEnvironmentId() { /* "env;rm" → ArgumentException */ }
[Fact] public void TryTransition_OnlyFromExpectedStatus() { /* Draft→Queued true; again false */ }
[Fact] public void TryTransition_Concurrent_OnlyOneWins() { /* Parallel.For 8 → exactly one true */ }
[Fact] public void SetLead_SetsSessionAndBuilding() { }
[Fact] public void WriteUnits_IsHandEditableJson() { /* file contains "\"stage\": \"Implement\"" */ }
[Fact] public void ListActive_SkipsDraftDoneAndMalformed() { }
[Fact] public void Inbox_HasSourceKey_TrueAfterAnswer() { }
```

- [ ] **Step 2: Run them; expect failures.**
- [ ] **Step 3: Implement.** Reuse `AtomicFile.Write` and each store's existing lock.
- [ ] **Step 4: Run the full suite; expect all to pass.** Plan 1 tests must still pass.
- [ ] **Step 5: Commit** — `feat(core): run cursor, atomic transitions, card source keys, model/env config`

---

### Task 4: Personas and lead prompt

**Files:**
- Create: `personas/lead.md`, `personas/implementer.md`, `personas/reviewer.md`, `personas/validator.md`
- Create: `runtime/src/Fyrd.Core/Orchestration/LeadPrompt.cs`
- Test: `runtime/tests/Fyrd.Core.Tests/LeadPromptTests.cs`

**Interfaces:**
- Consumes: `SpecRun`, `FyrdConfig`, `DecisionMemory.ReadFor`.
- Produces:
  - `sealed record Personas(string Lead, string Implementer, string Reviewer, string Validator)` with `static Personas Load(string dir)`. It throws `FileNotFoundException` naming the missing file.
  - `static class LeadPrompt { string Build(SpecRun run, string specMarkdown, string memory, FyrdConfig config, Personas personas) }`.

`lead.md` is a template. `Build` replaces these placeholders: `{{SPEC_ID}}`, `{{REPO}}`, `{{SPEC}}`, `{{MEMORY}}`, `{{SQUAD_CAP}}`, `{{PEER_MODEL}}`, `{{ENVIRONMENT_ID}}`, `{{IMPLEMENTER_PERSONA}}`, `{{REVIEWER_PERSONA}}`, `{{VALIDATOR_PERSONA}}`. Write the persona prose yourself; it must contain the following.

`lead.md` must:
- Break the spec into parallel units, and emit `fyrd-units` first and again whenever a unit's status changes.
- Run at most `{{SQUAD_CAP}}` squads at once, and obey any later `fyrd-config`.
- For each unit, create peers in order (implementer, then reviewer, then validator) with `create_session`, using:
  - `source_url` `https://github.com/{{REPO}}`, `environment_id` `{{ENVIRONMENT_ID}}`, `model` `{{PEER_MODEL}}`, `permission_mode` `auto`
  - tags `fyrd`, `fyrd-spec-{{SPEC_ID}}`, `fyrd-role-<role>`, `fyrd-unit-<unitId>`
  - `outcome_branch` `fyrd/{{SPEC_ID}}/unit/<unitId>` for the implementer
  - `append_system_prompt` = that role's persona
- Settle questions using the remembered decisions (`{{MEMORY}}`) and its own judgment first. Ask the owner only with a `fyrd-question` block, then end its turn.
- Treat `fyrd-answer` as the owner's decision.
- Merge finished units into `fyrd/{{SPEC_ID}}/integration`, do a final review, and archive finished peer sessions.
- End with `fyrd-done` (or `fyrd-failed`).
- Safety rules (verbatim intent from the spike): never force-push; push only branches under `fyrd/{{SPEC_ID}}/` (`integration` and `unit/<unitId>`), never a bare `fyrd/{{SPEC_ID}}`; only create, message, interrupt or archive sessions it created for this spec; never merge a PR.

`implementer.md`, `reviewer.md` and `validator.md` define each role: implement and test, then push to the unit branch; review the unit branch diff and report findings to the lead with `send_message`; run the tests and verify behavior, then report pass or fail to the lead. Each states that it reports only to its lead and asks the lead, not the owner.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Build_FillsEveryPlaceholder() { /* result contains no "{{" */ }
[Fact] public void Build_ContainsProtocolAndSafetyRules()
{ /* contains "fyrd-units","fyrd-question","fyrd-done","fyrd-failed","fyrd-answer","fyrd-config",
     "force-push","fyrd/20261030-x","fyrd-spec-20261030-x","permission_mode","auto","servicetitan/titan-lens-ossie" */ }
[Fact] public void Build_InjectsSquadCapAndMemory() { /* cap 3 → "3"; memory text present */ }
[Fact] public void Personas_Load_MissingFile_NamesIt() { }
[Fact] public void ShippedPersonas_LoadFromRepo() { /* Personas.Load("<repo>/personas") succeeds; each non-empty */ }
```

- [ ] **Step 2: Run them; expect failures.**
- [ ] **Step 3: Write the four persona files and `LeadPrompt`.**
- [ ] **Step 4: Run them; expect all to pass.**
- [ ] **Step 5: Commit** — `feat: role personas and lead prompt builder`

---

### Task 5: Attack launches the lead

**Files:**
- Create: `runtime/src/Fyrd.Core/Orchestration/SpecLauncher.cs`
- Modify: `runtime/src/Fyrd.Server/Endpoints/SpecEndpoints.cs`, `Program.cs`, `scripts/fyrd.mjs`
- Test: `runtime/tests/Fyrd.Core.Tests/SpecLauncherTests.cs`, `runtime/tests/Fyrd.Server.Tests/ApiTests.cs`, `FyrdFactory.cs`, a `FakeCloudSessions` test double in `Fyrd.Core.Tests`

**Interfaces:**
- Consumes: Tasks 1, 3, 4.
- Produces:
  - `sealed class SpecLauncher(SpecStore specs, ConfigStore config, DecisionMemory memory, ICloudSessions cloud, Personas personas)` with `Task<SpecRun> LaunchAsync(string specId, CancellationToken ct)`.
    - Before calling, the caller has already done `TryTransition(Draft, Queued)`.
    - `LaunchAsync` creates the lead with:
      - title `"Fyrd lead · <spec title>"`
      - tags `fyrd`, `fyrd-spec-<id>`, `fyrd-role-lead`
      - `config.EnvironmentId` and `config.LeadModel`
      - `Source = new SessionSource("https://github.com/<repo>", null)`
      - `PermissionMode = "auto"`
      - `FirstMessage = LeadPrompt.Build(...)`
    - It then calls `SetLead`.
    - On any exception it calls `TryTransition(Queued, Draft)` and rethrows.
  - `POST /api/specs/{id}/attack`:
    - 409 if `EnvironmentId` is null, with detail "Set the cloud environment in Settings".
    - 409 if `TryTransition` fails.
    - 502 ProblemDetails if launching fails, with the API exception's message (never the body).
    - 200 with the `Building` run on success.
  - `Program.cs`:
    - registers `ClaudeCredentials`, an `HttpClient` for `CloudSessionClient`, `ICloudSessions`, `Personas` (from `Fyrd:PersonasDir`, default `../../../personas` relative to the content root) and `SpecLauncher`
    - adds `UseExceptionHandler` that returns problem+json for unexpected errors
  - `FyrdFactory` replaces `ICloudSessions` with the fake.
  - `fyrd.mjs` passes `--Fyrd:PersonasDir <repoRoot>/personas`.

- [ ] **Step 1: Write failing tests**

```csharp
// SpecLauncherTests (FakeCloudSessions records CreateSessionRequest)
[Fact] public async Task Launch_CreatesLeadWithAutoModeTagsAndRepo()
{ /* PermissionMode=="auto"; Tags ⊇ {"fyrd","fyrd-spec-<id>","fyrd-role-lead"};
     Source.RepoUrl=="https://github.com/servicetitan/titan-lens-ossie"; Model=="claude-opus-5-5";
     run.Status==Building; run.SessionId==fake id */ }
[Fact] public async Task Launch_ApiFailure_RevertsToDraft() { }
// ApiTests
[Fact] public async Task Attack_WithoutEnvironment_Returns409() { }
[Fact] public async Task Attack_Concurrent_CreatesOneLead() { /* 2 parallel POSTs → one 200, one 409; fake saw 1 create */ }
[Fact] public async Task Attack_LaunchFails_Returns502_AndSpecIsDraft() { }
```

- [ ] **Step 2: Run them; expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the full suite and `node --test "scripts/*.test.mjs"`; expect all to pass.**
- [ ] **Step 5: Commit** — `feat: attack launches the lead cloud session`

---

### Task 6: SpecRunner poll loop

**Files:**
- Create: `runtime/src/Fyrd.Core/Orchestration/SpecRunner.cs`, `Orchestration/RunnerState.cs`, `runtime/src/Fyrd.Server/SpecRunnerService.cs`
- Modify: `Program.cs`
- Test: `runtime/tests/Fyrd.Core.Tests/SpecRunnerTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3, `InboxStore`, the `FakeCloudSessions` test double.
- Produces:
  - `sealed class RunnerState` (singleton) with `bool SignedOut`, `string? LastError`, `DateTimeOffset? LastPollAt`, and `IReadOnlyDictionary<string, CloudSession> Sessions` (latest per session id, for the Board).
  - `sealed class SpecRunner(SpecStore specs, InboxStore inbox, ICloudSessions cloud, RunnerState state, TimeProvider time)` with `Task PollOnceAsync(CancellationToken ct)`. For each `ListActive()` spec with a `SessionId`, it does the following.
    1. **Events:** list the lead's events after `LastEventId`, following all pages. For each new `assistant` event, run `Protocol.Parse` on its `Text`:
       - `Units` → `WriteUnits`, keeping existing stage/state for known ids.
       - Each `Question` → `inbox.Add(..., sourceKey: "<specId>:<question.Id>")`, unless `HasSourceKey`. `AllowFreeText` is true; `unit` → `UnitId`.
       - `Done` → `Finish(id, ReadyForHandoff, branch, null)`.
       - `Failed` → `Finish(id, Failed, null, reason)`.
       - Then `SetCursor(lastSeenUuid)`.
    2. **Lead status:** `GetAsync(lead)` goes into `state.Sessions`. If the lead is `idle`, `Summary.NeedsAction` is non-empty, and the latest assistant event had no question, add the card "Lead needs attention: <needs_action>" with `sourceKey "<specId>:attention:<lastEventId>"` and details `https://claude.ai/code/<sessionId>`.
    3. **Peers:** `ListByTagAsync("fyrd-spec-<id>")` goes into `state.Sessions`. For each unit, take its newest peer session by `created_at`; its `fyrd-role-*` tag gives the stage, and its status gives the state, per "Decisions made in this plan". `WaitingOnOwner` applies if `ListOpenFor(spec)` has a card with that `UnitId`. Then `WriteUnits`.
    - On `CloudSessionAuthException` or `ClaudeAuthMissingException`: set `state.SignedOut = true` and stop this pass.
    - On success: set `SignedOut = false`.
    - Any other exception for a spec goes into `state.LastError`, and the pass continues with the next spec.
  - `SpecRunnerService : BackgroundService` calls `PollOnceAsync` every 10 s, or every 60 s while `SignedOut`, and logs exceptions without crashing.

- [ ] **Step 1: Write failing tests** (the fake serves scripted event pages and sessions)

```csharp
[Fact] public async Task Poll_UnitsBlock_WritesUnits() { }
[Fact] public async Task Poll_QuestionBlock_CreatesCardWithSourceKey() { /* card.SourceKey == "<id>:q-color", AllowFreeText */ }
[Fact] public async Task Poll_SameQuestionTwice_OneCard() { /* same event returned on two polls → 1 card */ }
[Fact] public async Task Poll_ResumesFromStoredCursor() { /* second runner instance passes after_id == stored LastEventId */ }
[Fact] public async Task Poll_PeerSessions_SetStageAndState()
{ /* unit a: implementer idle → Implement/Idle; unit b: reviewer working → Review/Running; open card for b → WaitingOnOwner */ }
[Fact] public async Task Poll_DoneBlock_SetsReadyForHandoffAndBranch() { }
[Fact] public async Task Poll_NeedsActionWithoutQuestion_CreatesAttentionCard() { }
[Fact] public async Task Poll_AuthFailure_SetsSignedOut_ThenRecovers() { }
[Fact] public async Task Poll_OneSpecThrows_OthersStillPolled() { }
```

- [ ] **Step 2: Run them; expect failures.**
- [ ] **Step 3: Implement** `SpecRunner`, `RunnerState` and `SpecRunnerService`, and register them.
- [ ] **Step 4: Run the full suite; expect all to pass.**
- [ ] **Step 5: Commit** — `feat: poll lead and peer sessions into units, inbox and status`

---

### Task 7: Answer and config relay

**Files:**
- Create: `runtime/src/Fyrd.Core/Orchestration/LeadRelay.cs`
- Modify: `runtime/src/Fyrd.Server/Endpoints/InboxEndpoints.cs`, `Endpoints/ConfigEndpoints.cs`, `Program.cs`
- Test: `runtime/tests/Fyrd.Core.Tests/LeadRelayTests.cs`, `ApiTests.cs`

**Interfaces:**
- Produces: `sealed class LeadRelay(SpecStore specs, ICloudSessions cloud)` with two methods.
  - `Task RelayAnswerAsync(InboxCard answered, CancellationToken ct)`:
    - For a `SourceKey` of `<specId>:<questionId>`, it sends `Protocol.FormatAnswer(questionId, answer.Text)` to the spec's lead.
    - For `<specId>:attention:*`, it sends the plain answer text.
    - Without a key or a lead, it does nothing.
  - `Task RelayConfigAsync(int squadCap, CancellationToken ct)` sends `Protocol.FormatConfig(cap)` to every `ListActive()` lead.
  - Endpoint behavior:
    - Answer: the answer is saved first. If the relay then fails, it still returns 200, with an extra `relayError` string in the body, and the console shows it on the Board banner. The card stays answered, because memory was recorded.
    - `PUT /api/config` relays only when `SquadCap` changed.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task RelayAnswer_SendsFyrdAnswerBlockToLead() { /* fake saw message containing "```fyrd-answer" and "\"id\":\"q-color\"" */ }
[Fact] public async Task RelayAnswer_AttentionCard_SendsPlainText() { }
[Fact] public async Task RelayAnswer_NoSourceKey_SendsNothing() { }
[Fact] public async Task RelayConfig_OnlyActiveLeads() { }
[Fact] public async Task AnswerEndpoint_RelayFails_StillAnswered_Returns200WithRelayError() { }
[Fact] public async Task PutConfig_SameCap_DoesNotRelay() { }
```

- [ ] **Step 2: Run them; expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the full suite; expect all to pass.**
- [ ] **Step 5: Commit** — `feat: relay owner answers and squad cap to the lead`

---

### Task 8: Board aggregate and console

**Files:**
- Create: `runtime/src/Fyrd.Server/Endpoints/BoardEndpoints.cs`, `console/src/board/SettingsPanel.tsx`
- Modify: `Program.cs`, `console/src/{api.ts,types.ts,App.tsx}`, `console/src/board/{BoardTab.tsx,SquadCapControl.tsx}`, `console/src/inbox/InboxCard.tsx`
- Test: `ApiTests.cs`, `console/src/board/BoardTab.test.tsx`, `console/src/board/SettingsPanel.test.tsx`, `console/src/App.test.tsx`

**Interfaces:**
- Produces: `GET /api/board` → `{ specs: BoardSpec[], signedOut: bool, lastError: string|null, lastPollAt: string|null, rateLimit: {status, resetsAt}|null }`.
  - `BoardSpec = { entry: SpecListEntry, units: Unit[], lead: {sessionId, url, status, contextUsed, contextMax}|null, peers: [{sessionId, url, role, unitId, status}] }`.
  - URLs are `https://claude.ai/code/<sessionId>`.
  - `contextUsed`/`contextMax` come from `external_metadata.context_usage.used_tokens`/`max_tokens`. `rateLimit` comes from any session's `external_metadata.rate_limit_info`.
  - A malformed spec still appears with its error.
- Console:
  - The Board polls `/api/board` instead of one `getSpec` per spec.
  - Each spec shows its lead link and status, a context bar ("52k / 1M tokens"), and its branch once ready.
  - Each unit row links its peer sessions, by role.
  - A top banner shows "Signed out of Claude — run `claude auth login`" when `signedOut`, and an amber banner shows `lastError` or a relay error.
  - A rate-limit chip shows its status and reset time.
  - `SettingsPanel` holds the squad cap (moved from `SquadCapControl`), the environment ID input, and the lead/peer model inputs. It saves with `PUT /api/config` and shows the server's 400 detail.
  - The Attack button shows the 409/502 detail inline.
  - `InboxCard` renders `details` URLs as links.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public async Task Board_IncludesUnitsLeadAndPeerLinks() { /* seed RunnerState + stores; url == "https://claude.ai/code/session_x" */ }
[Fact] public async Task Board_MalformedSpecStillListed() { }
```
```tsx
it("shows the signed-out banner", …); it("renders peer session links per unit", …);
it("shows attack 409 detail inline", …); it("settings saves environment id and shows 400 detail", …);
it("renders a context bar from lead usage", …); it("inbox card details URL is a link", …);
```

- [ ] **Step 2: Run them; expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run all three suites and `npm run build`; expect all to pass.**
- [ ] **Step 5: Commit** — `feat: board aggregate with live sessions, settings, banners`

---

### Task 9: Owner smoke run (manual, real cloud)

The owner runs this; agents can't, because it uses the owner's claude.ai login. It creates real cloud sessions and pushes `fyrd/<specId>/*` branches to `servicetitan/titan-lens-ossie`.

- [ ] **Step 1:** In `main`, run `/fyrd:up`. In Settings, set the environment to `env_011v4b3WBhRMaGGti6Qw747c` (or another of the owner's environments).
- [ ] **Step 2:** Create a tiny spec, e.g. "Add a `docs/fyrd-smoke.md` file containing one sentence", and attack it.
- [ ] **Step 3:** Within about 2 minutes, the Board shows the lead link with status `Building`. Units appear after the lead emits `fyrd-units`. Peer links appear per unit, and the stage markers advance.
- [ ] **Step 4:** If a card appears, answer it, and confirm the lead continues in its claude.ai session.
- [ ] **Step 5:** The spec reaches `ReadyForHandoff` with branch `fyrd/<specId>/integration`. Confirm the branch exists on GitHub and contains the file.
- [ ] **Step 6:** Record what happened (time taken, cards, anything odd) in `docs/superpowers/spikes/2026-10-30-plan2-smoke.md`. Delete the `fyrd/<specId>/*` branches after confirming with the owner.
