# Fyrd Foundation (Plan 1 of 4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `/fyrd:up` starts a local C# backend and React console. In the console you can write or upload a spec, see it on the Board, answer Inbox cards (remembered by default), and adjust the squad cap. Plan 1 also ships the `CloudSessionClient` that talks to Claude Code cloud sessions, plus a spike that verifies the assumptions Plan 2 depends on.

**Architecture:** A Node launcher script (no dependencies), invoked by plugin slash commands, builds the app, starts an ASP.NET Core minimal-API server bound to 127.0.0.1, and records the server's PID and port. The server owns all state as hand-editable JSON and Markdown under `~/.fyrd/data/` and serves the built React console as static files. The console polls the server's JSON API. `CloudSessionClient` wraps the undocumented cloud-session API (design §11) behind one class; nothing in this plan calls it from the server yet.

**Tech Stack:** .NET 10 (`net10.0`), ASP.NET Core minimal APIs, `HttpClient`, xUnit, `Microsoft.AspNetCore.Mvc.Testing`; React 18 + TypeScript + Vite, Vitest + Testing Library; Node 24 (`node:test`) for the launcher. No Anthropic SDK.

**Spec:** `docs/superpowers/specs/2026-10-29-fyrd-design.md`

## Roadmap (this plan is 1 of 4)

The design covers several subsystems, so it is split into four plans. Each one ships working software:

1. **Foundation (this plan):** runtime, state, console shell, launcher commands, `CloudSessionClient`, and the cloud-session spike.
2. **Orchestration:** role personas as prompt files, attack creates the lead cloud session, the lead creates one cloud session per peon with the `claude-code-remote` tools, Fyrd polls tagged sessions into units/Board, `fyrd-question` blocks feed the Inbox, squad cap passed to the lead, rate-limit and context usage on the Board. Written after the Task 8 spike findings.
3. **Research and memory:** the research stage (researcher threads, free-text Inbox conversation, finalizing the spec), the owner-proxy persona, and injecting remembered decisions into sessions.
4. **Handoff:** integration branch, final review, the "check out this branch" handoff card, and opening the PR host-side on approval.

## Global Constraints

- All state lives under `~/.fyrd/data/` (overridable with the `FYRD_HOME` env var or the `Fyrd:Home` config key). Files are indented JSON or Markdown that a person can edit by hand.
- Runtime state that is not data (the PID/port file) lives under `~/.fyrd/run/`.
- The server binds `127.0.0.1` only.
- Default port 4870. If it is busy, try the next 10 ports.
- Minimal dependencies: no packages beyond those listed in Tech Stack.
- The attack is started from the console. There is no `/fyrd:attack` command.
- Squad cap: default **4**, valid range **1–8** (8 squads × 3 roles = 24 concurrent peon sessions, a ceiling chosen to stay inside subscription rate limits), adjustable from the console.
- The claude.ai OAuth token is read from `~/.claude/.credentials.json` on each request and is never logged, stored elsewhere, or sent anywhere but `https://api.anthropic.com`.
- Decisions are remembered by default; default memory scope is the spec's repo.
- Fyrd never merges a PR.
- Plugin commands: `/fyrd:up`, `/fyrd:down`, `/fyrd:status`.

## Review Focus

1. **A hand-edited file is malformed** (bad JSON in one spec's `run.json`). Expected: that spec shows as an error entry; every other spec still lists; the server does not crash. Test: Task 2 `List_SkipsMalformedRun_AndReportsIt`, Task 4 `GetSpecs_WithOneMalformed_Returns200`.
2. **A card is answered twice** (double click, two tabs). Expected: the second answer gets 409; memory records it once. Test: Task 3 `Answer_Twice_Throws_AndRecordsMemoryOnce`, Task 4 `AnswerCard_Twice_Returns409`.
3. **`/fyrd:up` runs while already up, or after a crash left a stale PID file.** Expected: reuse the live server; replace a stale one. Test: Task 6 `isServerAlive` tests.
4. **Port 4870 is taken by another program.** Expected: the next free port is used and recorded. Test: Task 6 `findFreePort skips busy port`.
5. **Hostile or odd spec input:** a title with slashes, `..`, or unicode; an id with path traversal in the URL; an empty spec. Expected: safe slug; traversal rejected with 400; empty markdown rejected with 400. Test: Task 2 `Create_SlugifiesUnsafeTitle`, `Get_RejectsTraversalId`; Task 4 `CreateSpec_EmptyMarkdown_Returns400`.

---

## File Structure

```
runtime/
  Fyrd.sln
  src/Fyrd.Core/                 # domain + file-backed stores, no web deps
    Fyrd.Core.csproj
    FyrdPaths.cs                 # resolves ~/.fyrd/data and subfolders
    Json.cs                      # shared JsonSerializerOptions (indented, camelCase, enums as strings)
    Config/FyrdConfig.cs         # record + ConfigStore
    Specs/SpecModels.cs          # SpecRun, Unit, enums
    Specs/SpecStore.cs
    Specs/SpecIds.cs             # slug + id validation
    Inbox/InboxModels.cs         # InboxCard, CardAnswer, MemoryScope
    Inbox/InboxStore.cs
    Memory/DecisionMemory.cs
    CloudSessions/ClaudeCredentials.cs   # reads token + org uuid from Claude Code's files
    CloudSessions/CloudSessionModels.cs  # request/response records
    CloudSessions/CloudSessionClient.cs  # the only code that knows the undocumented API
  src/Fyrd.Server/
    Fyrd.Server.csproj
    Program.cs                   # DI + endpoint mapping + static files
    Endpoints/SpecEndpoints.cs
    Endpoints/InboxEndpoints.cs
    Endpoints/ConfigEndpoints.cs
    wwwroot/                     # console build output (gitignored)
  tests/Fyrd.Core.Tests/
  tests/Fyrd.Server.Tests/
console/                         # Vite React app
  package.json, vite.config.ts, tsconfig.json, index.html
  src/main.tsx, App.tsx, api.ts, types.ts
  src/inbox/InboxTab.tsx, InboxCard.tsx
  src/board/BoardTab.tsx, SquadCapControl.tsx
  src/specs/NewSpecForm.tsx
  src/**/*.test.tsx
scripts/
  fyrd.mjs                       # up | down | status
  lib.mjs                        # pure helpers (ports, pid file, staleness)
  lib.test.mjs
commands/up.md, down.md, status.md
spikes/cloud-cli/                # existing probes (PtyProbe, probe-create-send.sh)
spikes/cloud-sessions/           # Task 8 spike console app
docs/superpowers/spikes/2026-10-29-cloud-sessions.md
```

## State layout (hand-editable)

```
~/.fyrd/data/
  config.json                         { "squadCap": 4 }
  specs/<specId>/spec.md              the spec text
  specs/<specId>/run.json             SpecRun
  specs/<specId>/units.json           Unit[]  (empty until Plan 2)
  inbox/<cardId>.json                 InboxCard
  memory/global.md                    remembered decisions, all repos
  memory/<owner>__<repo>.md           remembered decisions, one repo
~/.fyrd/run/server.json               { "pid": 1234, "port": 4870, "startedAt": "..." }
```

---

### Task 1: Solution skeleton, paths, and config

**Files:**
- Create: `runtime/Fyrd.sln`, `runtime/src/Fyrd.Core/Fyrd.Core.csproj`, `runtime/src/Fyrd.Core/FyrdPaths.cs`, `runtime/src/Fyrd.Core/Json.cs`, `runtime/src/Fyrd.Core/Config/FyrdConfig.cs`
- Create: `runtime/tests/Fyrd.Core.Tests/Fyrd.Core.Tests.csproj`, `runtime/tests/Fyrd.Core.Tests/ConfigStoreTests.cs`, `runtime/tests/Fyrd.Core.Tests/TempHome.cs`
- Modify: `.gitignore` (add `bin/`, `obj/`, `runtime/src/Fyrd.Server/wwwroot/`, `console/node_modules/`, `spikes/cloud-sessions/out/`)

**Interfaces:**
- Produces:
  - `sealed class FyrdPaths(string root)` with `Root`, `ConfigFile`, `SpecsDir`, `InboxDir`, `MemoryDir` (all `string`), `string SpecDir(string specId)`, `static FyrdPaths Resolve(string? configured)`: uses `configured`, else env `FYRD_HOME`, else `~/.fyrd/data`. The constructor creates the directories.
  - `static class Json { public static readonly JsonSerializerOptions Options; }`: indented, camelCase, `JsonStringEnumConverter`.
  - `sealed record FyrdConfig(int SquadCap = 4)` with `const int MinSquadCap = 1, MaxSquadCap = 8`.
  - `sealed class ConfigStore(FyrdPaths paths)`: `FyrdConfig Load()` (returns defaults if the file is missing), `void Save(FyrdConfig config)` (throws `ArgumentOutOfRangeException` if `SquadCap` is outside 1–8).
  - Test helper `sealed class TempHome : IDisposable` with `FyrdPaths Paths`; it deletes its folder on dispose.

- [ ] **Step 1: Create the solution and projects**

Run (from `runtime/`): `dotnet new sln -n Fyrd && dotnet new classlib -n Fyrd.Core -o src/Fyrd.Core -f net10.0 && dotnet new xunit -n Fyrd.Core.Tests -o tests/Fyrd.Core.Tests -f net10.0 && dotnet sln add src/Fyrd.Core tests/Fyrd.Core.Tests && dotnet add tests/Fyrd.Core.Tests reference src/Fyrd.Core`
Then delete the template `Class1.cs` and `UnitTest1.cs`. Enable `<Nullable>enable</Nullable>` in both projects.
Expected: `dotnet build` succeeds.

- [ ] **Step 2: Write failing tests** in `ConfigStoreTests.cs`

```csharp
[Fact] public void Load_MissingFile_ReturnsDefaultCap4()
{ using var h = new TempHome(); Assert.Equal(4, new ConfigStore(h.Paths).Load().SquadCap); }

[Fact] public void Save_ThenLoad_RoundTrips()
{ using var h = new TempHome(); var s = new ConfigStore(h.Paths);
  s.Save(new FyrdConfig(6)); Assert.Equal(6, s.Load().SquadCap);
  Assert.Contains("\"squadCap\": 6", File.ReadAllText(h.Paths.ConfigFile)); }

[Theory, InlineData(0), InlineData(9)]
public void Save_OutOfRange_Throws(int cap)
{ using var h = new TempHome();
  Assert.Throws<ArgumentOutOfRangeException>(() => new ConfigStore(h.Paths).Save(new FyrdConfig(cap))); }

[Fact] public void Resolve_PrefersConfiguredOverEnv()
{ using var h = new TempHome(); Assert.Equal(h.Paths.Root, FyrdPaths.Resolve(h.Paths.Root).Root); }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test runtime/Fyrd.sln`
Expected: compile errors (types not defined).

- [ ] **Step 4: Implement `FyrdPaths`, `Json`, `FyrdConfig`, `ConfigStore`, `TempHome`**

`TempHome` uses `Path.Combine(Path.GetTempPath(), "fyrd-test-" + Guid.NewGuid())`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test runtime/Fyrd.sln`
Expected: 5 passed.

- [ ] **Step 6: Commit**

```bash
git add runtime .gitignore
git commit -m "feat(runtime): solution skeleton, data paths, config store"
```

---

### Task 2: Spec store

**Files:**
- Create: `runtime/src/Fyrd.Core/Specs/SpecModels.cs`, `Specs/SpecIds.cs`, `Specs/SpecStore.cs`
- Test: `runtime/tests/Fyrd.Core.Tests/SpecStoreTests.cs`

**Interfaces:**
- Consumes: `FyrdPaths`, `Json.Options`.
- Produces:
  - `enum SpecStatus { Draft, Queued, Researching, Building, FinalReview, ReadyForHandoff, PrOpened, Failed }`
  - `enum UnitStage { Implement, Review, Validate, Done }`, `enum UnitState { Running, Idle, WaitingOnOwner, Blocked, Done }`
  - `sealed record SpecRun(string Id, string Title, string Repo, SpecStatus Status, DateTimeOffset CreatedAt, string? SessionId = null, long CostCents = 0)`
  - `sealed record Unit(string Id, string SpecId, string Title, UnitStage Stage, UnitState State)`
  - `sealed record SpecListEntry(SpecRun? Run, string Id, string? Error)`: `Run` is null and `Error` is set when `run.json` is unreadable.
  - `static class SpecIds`: `string NewId(string title, DateTimeOffset now)` → `yyyyMMdd-<slug>`, where slug is lowercase ASCII `[a-z0-9-]`, runs of anything else collapse to `-`, max 40 chars, and an empty slug becomes `spec`. `bool IsValid(string id)` → matches `^[0-9]{8}-[a-z0-9-]{1,48}$`.
  - `sealed class SpecStore(FyrdPaths paths, TimeProvider time)`:
    - `SpecRun Create(string title, string repo, string markdown)`: throws `ArgumentException` on empty markdown or a repo that does not match `^[\w.-]+/[\w.-]+$`; adds `-2`, `-3`, … to the id on collision; writes `spec.md`, `run.json`, and `units.json` (`[]`).
    - `IReadOnlyList<SpecListEntry> List()`: newest first.
    - `SpecRun? Get(string id)`: throws `ArgumentException` if `!SpecIds.IsValid(id)`.
    - `string ReadMarkdown(string id)`
    - `SpecRun UpdateStatus(string id, SpecStatus status)`
    - `IReadOnlyList<Unit> ListUnits(string id)`

- [ ] **Step 1: Write failing tests.** Add `sealed class FixedTime(DateTimeOffset now) : TimeProvider` (overrides `GetUtcNow()`) to `TempHome.cs`; no extra package. `Store(h, "2026-10-29")` builds a `SpecStore` with that date.

```csharp
[Fact] public void Create_WritesHandEditableFiles()
{ using var h = new TempHome(); var s = Store(h, "2026-10-29");
  var run = s.Create("Add export button", "servicetitan/titan-lens-ossie", "# Export\nDo it.");
  Assert.Equal("20261029-add-export-button", run.Id);
  Assert.Equal(SpecStatus.Draft, run.Status);
  Assert.Equal("# Export\nDo it.", File.ReadAllText(Path.Combine(h.Paths.SpecDir(run.Id), "spec.md")));
  Assert.Equal("[]", File.ReadAllText(Path.Combine(h.Paths.SpecDir(run.Id), "units.json")).Trim()); }

[Fact] public void Create_SameTitleTwice_GetsSuffix()
{ /* second id == "20261029-add-export-button-2" */ }

[Fact] public void Create_SlugifiesUnsafeTitle()
{ /* title "../../etc/Passwd ✨ now" -> id "20261029-etc-passwd-now"; file stays under SpecsDir */ }

[Fact] public void Create_EmptyMarkdown_Throws() { /* "  " -> ArgumentException */ }

[Fact] public void Create_BadRepo_Throws() { /* "not a repo" -> ArgumentException */ }

[Fact] public void Get_RejectsTraversalId()
{ /* Assert.Throws<ArgumentException>(() => s.Get("..\\..\\x")) */ }

[Fact] public void List_SkipsMalformedRun_AndReportsIt()
{ /* create 2 specs, overwrite one run.json with "{nope"; List() has 2 entries:
     one with Run != null, one with Run == null and Error containing "run.json" */ }

[Fact] public void UpdateStatus_Persists() { /* Queued survives a new SpecStore instance */ }

[Fact] public void HandEditedTitle_IsRead()
{ /* edit "title" inside run.json with File.WriteAllText; Get returns edited title */ }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test runtime/Fyrd.sln --filter SpecStoreTests`
Expected: compile errors.

- [ ] **Step 3: Implement the models, `SpecIds`, and `SpecStore`**

Write each file to a temp file, then `File.Move(..., overwrite: true)`, so a crash never leaves half a file. `List()` catches `JsonException` and `IOException` for each spec.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test runtime/Fyrd.sln --filter SpecStoreTests`
Expected: 9 passed.

- [ ] **Step 5: Commit**

```bash
git add runtime
git commit -m "feat(core): file-backed spec store with safe ids"
```

---

### Task 3: Inbox and decision memory

**Files:**
- Create: `runtime/src/Fyrd.Core/Inbox/InboxModels.cs`, `Inbox/InboxStore.cs`, `Memory/DecisionMemory.cs`
- Test: `runtime/tests/Fyrd.Core.Tests/InboxStoreTests.cs`, `DecisionMemoryTests.cs`

**Interfaces:**
- Consumes: `FyrdPaths`, `SpecStore.Get`, `Json.Options`.
- Produces:
  - `enum MemoryScope { Repo, Global }`
  - `sealed record CardAnswer(string Text, bool Remember, MemoryScope Scope, DateTimeOffset AnsweredAt)`
  - `sealed record InboxCard(string Id, string SpecId, string? UnitId, string Question, IReadOnlyList<string> Options, string? Recommendation, string? Reason, string? Details, bool AllowFreeText, DateTimeOffset CreatedAt, CardAnswer? Answer = null)`
  - `sealed class CardAlreadyAnsweredException(string cardId) : InvalidOperationException`
  - `sealed class DecisionMemory(FyrdPaths paths)`:
    - `void Record(string repo, MemoryScope scope, string question, string answer, DateTimeOffset at, string specId)`: appends to `memory/global.md` or `memory/<owner>__<repo>.md`.
    - `string ReadFor(string repo)`: the global file's content, then the repo file's content (empty string for a missing file).
  - `sealed class InboxStore(FyrdPaths paths, SpecStore specs, DecisionMemory memory, TimeProvider time)`:
    - `InboxCard Add(string specId, string? unitId, string question, IReadOnlyList<string> options, string? recommendation, string? reason, string? details, bool allowFreeText)`: id is `Guid.NewGuid().ToString("N")[..12]`.
    - `IReadOnlyList<InboxCard> ListOpen()`: unanswered, oldest first.
    - `InboxCard Answer(string cardId, string text, bool remember = true, MemoryScope scope = MemoryScope.Repo)`: throws `CardAlreadyAnsweredException`, `KeyNotFoundException`, or `ArgumentException` (empty text). Serialize answers with a `lock` so two concurrent requests can't both win.

Memory entry format (copy exactly):

```markdown
- **Q:** What kind of test coverage do you expect?
  **A:** Unit tests for all new logic; no e2e.
  _2026-10-29 · spec 20261029-add-export-button_
```

- [ ] **Step 1: Write failing tests**

```csharp
// InboxStoreTests
[Fact] public void Add_ThenListOpen_ReturnsCard() { /* Options, Recommendation round-trip */ }

[Fact] public void Answer_RemembersByDefault_InRepoFile()
{ /* answer "Unit tests only"; memory/servicetitan__titan-lens-ossie.md contains
     "**Q:** What kind of test coverage do you expect?" and "**A:** Unit tests only";
     memory/global.md does not exist */ }

[Fact] public void Answer_GlobalScope_WritesGlobalFile() { }

[Fact] public void Answer_RememberFalse_WritesNoMemory() { }

[Fact] public void Answer_RemovesFromListOpen() { }

[Fact] public void Answer_Twice_Throws_AndRecordsMemoryOnce()
{ /* second Answer throws CardAlreadyAnsweredException; repo file has exactly one "**Q:**" */ }

[Fact] public void Answer_UnknownCard_ThrowsKeyNotFound() { }

// DecisionMemoryTests
[Fact] public void ReadFor_ConcatenatesGlobalThenRepo() { }
[Fact] public void ReadFor_NoFiles_ReturnsEmpty() { }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test runtime/Fyrd.sln --filter "InboxStoreTests|DecisionMemoryTests"`
Expected: compile errors.

- [ ] **Step 3: Implement `InboxModels`, `DecisionMemory`, `InboxStore`**

- [ ] **Step 4: Run tests to verify they pass**

Expected: 9 passed.

- [ ] **Step 5: Commit**

```bash
git add runtime
git commit -m "feat(core): inbox cards with remember-by-default decision memory"
```

---

### Task 4: HTTP server

**Files:**
- Create: `runtime/src/Fyrd.Server/Fyrd.Server.csproj` (`Microsoft.NET.Sdk.Web`, `net10.0`), `Program.cs`, `Endpoints/SpecEndpoints.cs`, `Endpoints/InboxEndpoints.cs`, `Endpoints/ConfigEndpoints.cs`
- Create: `runtime/tests/Fyrd.Server.Tests/Fyrd.Server.Tests.csproj` (refs `Microsoft.AspNetCore.Mvc.Testing`), `ApiTests.cs`, `FyrdFactory.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–3.
- Produces (JSON uses `Json.Options` casing; enums are strings):

| Method | Path | Body | Success | Errors |
|---|---|---|---|---|
| GET | `/api/health` | – | `200 {"status":"ok"}` | – |
| GET | `/api/specs` | – | `200 SpecListEntry[]` | – |
| POST | `/api/specs` | `{title, repo, markdown}` | `201 SpecRun` | `400` invalid |
| GET | `/api/specs/{id}` | – | `200 {run, markdown, units}` | `400` bad id, `404` |
| POST | `/api/specs/{id}/attack` | – | `200 SpecRun` (status `Queued`) | `404`, `409` if not `Draft` |
| GET | `/api/inbox` | – | `200 InboxCard[]` (open only) | – |
| POST | `/api/inbox/{id}/answer` | `{text, remember?, scope?}` | `200 InboxCard` | `400`, `404`, `409` |
| GET | `/api/config` | – | `200 FyrdConfig` | – |
| PUT | `/api/config` | `{squadCap}` | `200 FyrdConfig` | `400` out of range |

  - `Program.cs` registers `FyrdPaths.Resolve(builder.Configuration["Fyrd:Home"])`, `TimeProvider.System`, and the stores as singletons. It serves `wwwroot` with `UseDefaultFiles` + `UseStaticFiles` and `MapFallbackToFile("index.html")` for non-`/api` paths. End `Program.cs` with `public partial class Program;` for the test factory.
  - Errors are `ProblemDetails`.
  - `FyrdFactory : WebApplicationFactory<Program>` sets `Fyrd:Home` to a `TempHome` root through `UseSetting`.
  - Until Plan 2, `attack` only marks the spec `Queued`. Plan 2 starts the session here.

- [ ] **Step 1: Create the projects and add them to the solution**

Run (from `runtime/`): `dotnet new web -n Fyrd.Server -o src/Fyrd.Server -f net10.0 && dotnet new xunit -n Fyrd.Server.Tests -o tests/Fyrd.Server.Tests -f net10.0 && dotnet sln add src/Fyrd.Server tests/Fyrd.Server.Tests && dotnet add src/Fyrd.Server reference src/Fyrd.Core && dotnet add tests/Fyrd.Server.Tests reference src/Fyrd.Server tests/Fyrd.Core.Tests && dotnet add tests/Fyrd.Server.Tests package Microsoft.AspNetCore.Mvc.Testing`
Expected: `dotnet build` succeeds.

- [ ] **Step 2: Write failing tests** in `ApiTests.cs` (`IClassFixture` is not used; each test builds its own `FyrdFactory` so homes don't share state)

```csharp
[Fact] public async Task Health_Ok() { /* 200, status "ok" */ }

[Fact] public async Task CreateSpec_ThenList_ShowsIt()
{ /* POST {title:"Add export", repo:"servicetitan/titan-lens-ossie", markdown:"# x"} -> 201;
     GET /api/specs contains run.title "Add export", status "Draft" */ }

[Fact] public async Task CreateSpec_EmptyMarkdown_Returns400() { }

[Fact] public async Task GetSpecs_WithOneMalformed_Returns200()
{ /* create 2, corrupt one run.json on disk; GET /api/specs -> 200 with 2 entries, one has error */ }

[Fact] public async Task GetSpec_TraversalId_Returns400() { /* GET /api/specs/..%2F..%2Fx */ }

[Fact] public async Task Attack_SetsQueued_ThenSecondAttackIs409() { }

[Fact] public async Task AnswerCard_RemembersByDefault()
{ /* seed a card via the InboxStore from factory.Services; POST answer {text:"Unit tests"};
     card gone from GET /api/inbox; repo memory file contains "**A:** Unit tests" */ }

[Fact] public async Task AnswerCard_Twice_Returns409() { }

[Fact] public async Task PutConfig_OutOfRange_Returns400_AndKeepsOld() { /* PUT 9 -> 400; GET -> 4 */ }

[Fact] public async Task UnknownApiPath_Returns404_NotIndexHtml()
{ /* GET /api/nope -> 404; the SPA fallback must not swallow /api paths */ }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test runtime/Fyrd.sln --filter ApiTests`
Expected: failures (endpoints missing).

- [ ] **Step 4: Implement `Program.cs` and the three endpoint files**

Each endpoint file exposes `static IEndpointRouteBuilder MapXxx(this IEndpointRouteBuilder app)`. Map `ArgumentException` to 400, `KeyNotFoundException` to 404, and `CardAlreadyAnsweredException` to 409 inside the handlers.

- [ ] **Step 5: Run all tests**

Run: `dotnet test runtime/Fyrd.sln`
Expected: all pass (Tasks 1–4).

- [ ] **Step 6: Commit**

```bash
git add runtime
git commit -m "feat(server): JSON API for specs, inbox, config"
```

---

### Task 5: Console (Inbox | Board)

**Files:**
- Create: `console/` via `npm create vite@latest console -- --template react-ts`, then trim the template's demo files.
- Create: `console/src/types.ts`, `api.ts`, `App.tsx`, `inbox/InboxTab.tsx`, `inbox/InboxCard.tsx`, `board/BoardTab.tsx`, `board/SquadCapControl.tsx`, `specs/NewSpecForm.tsx`, `test/setup.ts`
- Test: `console/src/App.test.tsx`, `inbox/InboxCard.test.tsx`, `board/BoardTab.test.tsx`, `specs/NewSpecForm.test.tsx`

**Interfaces:**
- Consumes: the Task 4 API table.
- Produces:
  - `types.ts` mirrors `SpecRun`, `Unit`, `SpecListEntry`, `InboxCard`, `FyrdConfig` (camelCase, enums as string unions).
  - `api.ts`: `getSpecs()`, `getSpec(id)`, `createSpec({title, repo, markdown})`, `attack(id)`, `getInbox()`, `answer(id, {text, remember, scope})`, `getConfig()`, `putConfig({squadCap})`. All are `async` and throw `Error(problem.detail ?? status)` on a non-2xx response.
  - `vite.config.ts`: `build.outDir = "../runtime/src/Fyrd.Server/wwwroot"`, `emptyOutDir: true`, dev proxy `/api` → `http://127.0.0.1:4870`, Vitest `environment: "jsdom"`, `setupFiles: "src/test/setup.ts"`.
  - `package.json` scripts: `dev`, `build` (`tsc -b && vite build`), `test` (`vitest run`).

Behavior (from the approved mockups):
- **Tabs:** "Inbox (N)" and "Board". Inbox is the default and shows its open-card count. The Board tab label shows the done count, like "Board · 1/6 done", when units exist.
- **InboxCard:** shows the spec and unit label, the question, one button per option, and the lead's pick as "Lead's pick: X — reason" when present. "Details" expands `details`. When `allowFreeText`, it also shows a text box and a Send button. A "Remember this" checkbox is **checked by default**, with a scope select (Repo / Everywhere) defaulting to Repo. Clicking an option sends immediately, then disables the card's buttons until the request settles (Review Focus 2).
- **BoardTab:** "New spec" button → `NewSpecForm`. One group per spec (a malformed spec appears as a red row with its error). Each spec shows its title, repo, status, an Attack button when `Draft`, and unit rows with three stage markers (implement · review · validate: `✓` done, `●` current, `○` not started) plus "waiting on you" when `WaitingOnOwner`. `SquadCapControl` is a number input from 1 to 8 with a Save button; an error message appears on a 400.
- **NewSpecForm:** title, repo (default `servicetitan/titan-lens-ossie`), a textarea, and an "Upload .md" file input that fills the textarea. Create calls `createSpec`.
- Polling: `App` refetches the inbox, specs, and config every 3 s and stops when the tab is hidden (`document.visibilityState`).
- Styling: plain CSS in `App.css`, light and dark via `prefers-color-scheme`. No UI library.

- [ ] **Step 1: Scaffold and install test dependencies**

Run: `npm create vite@latest console -- --template react-ts && cd console && npm i && npm i -D vitest jsdom @testing-library/react @testing-library/jest-dom @testing-library/user-event`
Expected: `npm run build` succeeds.

- [ ] **Step 2: Write failing tests** (mock `api.ts` with `vi.mock("./api")`)

```tsx
// App.test.tsx
it("opens on the Inbox tab with the open-card count", async () => {
  getInbox.mockResolvedValue([card({ question: "SSO provider?" })]);
  render(<App />);
  expect(await screen.findByRole("tab", { name: "Inbox (1)", selected: true })).toBeInTheDocument();
  expect(screen.getByText("SSO provider?")).toBeInTheDocument();
});

// InboxCard.test.tsx
it("one click answers with remember=true scope=Repo by default", async () => {
  /* click "Entra ID" -> answer(id, {text:"Entra ID", remember:true, scope:"Repo"}) */ });
it("disables buttons while the answer is in flight", async () => { /* second click no 2nd call */ });
it("shows lead's pick and reason", () => { /* "Lead's pick: Entra ID — other repos use it" */ });
it("free text sends typed answer when allowed", async () => { });
it("unchecking remember sends remember=false", async () => { });

// BoardTab.test.tsx
it("renders stage markers per unit", () => { /* Implement/Running -> "●○○" ; Validate -> "✓✓●" */ });
it("shows malformed spec as error row", () => { });
it("attack button only on Draft specs and calls attack(id)", async () => { });
it("squad cap save shows server error on 400", async () => { });

// NewSpecForm.test.tsx
it("uploading a .md fills the textarea", async () => { /* userEvent.upload(File(["# Hi"], "s.md")) */ });
it("create posts title, repo, markdown", async () => { });
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `cd console && npm test`
Expected: failures (components missing).

- [ ] **Step 4: Implement `types.ts`, `api.ts`, and the components**

- [ ] **Step 5: Run tests and build**

Run: `cd console && npm test && npm run build`
Expected: all pass; `runtime/src/Fyrd.Server/wwwroot/index.html` exists.

- [ ] **Step 6: Commit**

```bash
git add console
git commit -m "feat(console): inbox-first console with board, new spec, squad cap"
```

---

### Task 6: Launcher and plugin commands

**Files:**
- Create: `scripts/lib.mjs`, `scripts/fyrd.mjs`, `scripts/lib.test.mjs`, `commands/up.md`, `commands/down.md`, `commands/status.md`
- Delete: `commands/hello.md`, `skills/hello/`, `agents/example.md`, `hooks/hooks.json` (placeholders; Plans 2–3 add real ones)
- Modify: `README.md` (usage section), `.claude-plugin/plugin.json` and `marketplace.json` (`description`: "Build software with an army of specialized agents, managed from a one-click console.")

**Interfaces:**
- Produces (`lib.mjs`, pure or injectable for tests):
  - `runDir(home = os.homedir())` → `<home>/.fyrd/run`
  - `readServerFile(dir)` → `{pid, port, startedAt} | null` (null on a missing or malformed file)
  - `writeServerFile(dir, info)`, `removeServerFile(dir)`
  - `async isServerAlive(info, fetchImpl = fetch)` → `true` only if `process.kill(pid, 0)` succeeds **and** `GET http://127.0.0.1:<port>/api/health` returns `{status:"ok"}` within 1 s
  - `async findFreePort(start = 4870, tries = 10)` → the first port where `net.createServer().listen(port, "127.0.0.1")` succeeds; throws `Error("No free port in 4870-4879")` otherwise
  - `needsConsoleBuild(repoRoot)` → true if `wwwroot/index.html` is missing or older than the newest file under `console/src`
- `fyrd.mjs <up|down|status>`:
  - **up:** if the server is alive, print its URL and open it. Otherwise remove any stale file; run `npm ci && npm run build` in `console/` if `needsConsoleBuild`; run `dotnet build runtime/src/Fyrd.Server -c Release`; spawn `dotnet runtime/src/Fyrd.Server/bin/Release/net10.0/Fyrd.Server.dll --urls http://127.0.0.1:<port>` detached with stdio sent to `~/.fyrd/run/server.log`, `unref()`; poll health for up to 30 s; write the server file; open the browser (`cmd /c start "" <url>` on win32, `open` on darwin, `xdg-open` elsewhere). On a health timeout, print the last 20 lines of `server.log` and exit 1.
  - **down:** kill the PID if alive, remove the server file, and print "Fyrd stopped." (or "Fyrd was not running.").
  - **status:** if alive, print `Fyrd running at <url> · <n> specs · <m> decisions waiting`, using `/api/specs` and `/api/inbox`. Otherwise print "Fyrd is not running. Start it with /fyrd:up."
- Each command file has frontmatter with `description` and `allowed-tools: Bash(node:*)`, and its body tells Claude to run `node "${CLAUDE_PLUGIN_ROOT}/scripts/fyrd.mjs" <cmd>` and relay the output in one line.

- [ ] **Step 1: Write failing tests** in `scripts/lib.test.mjs` (`node:test` + `node:assert/strict`)

```js
test("readServerFile returns null for malformed json", ...);       // write "{nope"
test("isServerAlive false when pid is dead", ...);                   // pid 999999
test("isServerAlive false when health fails", ...);                  // own pid, fetchImpl rejects
test("isServerAlive true when pid alive and health ok", ...);        // own pid, fetchImpl -> {status:"ok"}
test("findFreePort skips busy port", ...);                           // occupy 4870 with net server -> 4871
test("findFreePort throws when all busy", ...);                      // start=X, tries=1, X occupied
test("needsConsoleBuild true when wwwroot missing", ...);            // temp repo root
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `node --test scripts/`
Expected: failures (module not found).

- [ ] **Step 3: Implement `lib.mjs`, `fyrd.mjs`, and the three commands; delete the placeholders; update the README and descriptions**

- [ ] **Step 4: Run the unit tests and the plugin validator**

Run: `node --test scripts/ && claude plugin validate .`
Expected: 7 passed; "Validation passed" (the icon warning is acceptable).

- [ ] **Step 5: Manual end-to-end check**

Run: `node scripts/fyrd.mjs up` → the browser opens on the Inbox tab. Create a spec on the Board and attack it → its status shows `Queued`. Run `node scripts/fyrd.mjs up` again → it prints the same URL and doesn't start a second server. Run `node scripts/fyrd.mjs status` → it shows 1 spec. Run `node scripts/fyrd.mjs down` → "Fyrd stopped." Then edit `~/.fyrd/data/config.json` by hand to `{"squadCap": 2}`, run `up`, and confirm the Board shows 2.

- [ ] **Step 6: Commit**

```bash
git add -A scripts commands skills agents hooks README.md .claude-plugin
git commit -m "feat(plugin): /fyrd:up, /fyrd:down, /fyrd:status launcher"
```

---


### Task 7: CloudSessionClient

This is the one class that knows the undocumented cloud-session API (design §11). It's tested against a fake `HttpMessageHandler` and fake credential files; this task makes no real network calls.

**Files:**
- Create: `runtime/src/Fyrd.Core/CloudSessions/ClaudeCredentials.cs`, `CloudSessions/CloudSessionModels.cs`, `CloudSessions/CloudSessionClient.cs`
- Test: `runtime/tests/Fyrd.Core.Tests/CloudSessionClientTests.cs`, `ClaudeCredentialsTests.cs`, `FakeHandler.cs`

**Interfaces:**
- Consumes: `Json.Options`.
- Produces:
  - `sealed record ClaudeAuth(string AccessToken, string OrganizationUuid, DateTimeOffset ExpiresAt)`
  - `sealed class ClaudeCredentials(string claudeHome)`: `claudeHome` defaults to the user profile folder.
    - `ClaudeAuth Read()` reads two files on every call:
      - `<claudeHome>/.claude/.credentials.json`: `claudeAiOauth.accessToken`, and `claudeAiOauth.expiresAt` in epoch ms.
      - `<claudeHome>/.claude.json`: `oauthAccount.organizationUuid`.
    - Throws `ClaudeAuthMissingException` (message: "Sign in to Claude Code with `claude auth login`.") when either file or field is missing.
  - `sealed record SessionSource(string RepoUrl, string? Revision)`
  - `sealed record CreateSessionRequest(string Title, IReadOnlyList<string> Tags, string EnvironmentId, string FirstMessage, string Model, string? AppendSystemPrompt, SessionSource? Source)`
  - `sealed record PostTurnSummary(string? StatusCategory, string? NeedsAction, string? RecentAction, string? StatusDetail)`
  - `sealed record CloudSession(string Id, string? Title, string? SessionStatus, string? ConnectionStatus, PostTurnSummary? Summary, IReadOnlyList<string> Tags, JsonElement Raw)`: `Raw` keeps the whole response so Plan 2 can read fields this record doesn't name yet.
  - `sealed record SessionEvent(string Uuid, string Type, DateTimeOffset CreatedAt, string? Text, JsonElement Raw)`: `Text` is the `text` content blocks joined together for `user` and `assistant` events, and null otherwise.
  - `sealed record EventPage(IReadOnlyList<SessionEvent> Events, bool HasMore, string? FirstId, string? LastId)`
  - `sealed class CloudSessionClient(HttpClient http, ClaudeCredentials credentials)`, with `BaseAddress` `https://api.anthropic.com`:
    - `Task<CloudSession> CreateAsync(CreateSessionRequest request, CancellationToken ct = default)`
    - `Task<CloudSession> GetAsync(string sessionId, CancellationToken ct = default)`
    - `Task<EventPage> ListEventsAsync(string sessionId, int limit = 100, string? afterId = null, string? beforeId = null, CancellationToken ct = default)`
    - `Task SendMessageAsync(string sessionId, string text, CancellationToken ct = default)`
  - `sealed class CloudSessionAuthException : Exception` for 401 and 403. `sealed class CloudSessionApiException(int status, string body) : Exception` for any other non-2xx response. Neither message ever contains the token.

Request shapes (copy exactly; from design §11):
- **Headers on every request:** `Authorization: Bearer <token>`, `x-organization-uuid`, `anthropic-version: 2023-06-01`, `anthropic-beta: ccr-byoc-2025-07-29`.
- **Create body:** `{"title", "tags", "environment_id", "events":[{"type":"event","data":<user event>}], "session_context":{"sources":[{"type":"git_repository","url","revision"}] or [], "outcomes":[], "environment_variables":{}, "model", "append_system_prompt"?}}`.
- **User event:** `{"uuid":<new guid>, "session_id":"", "type":"user", "parent_tool_use_id":null, "message":{"role":"user","content":<text>}}`.
- **Send body:** `{"events":[<user event>]}`. This one is not wrapped in `type:"event"`.
- **Events:** `GET /v1/sessions/{id}/events?limit=&after_id=&before_id=`, which returns `{"data":[...], "has_more", "first_id", "last_id"}`.
- **Session ID check:** `sessionId` goes into a URL path, so it must match `^session_[A-Za-z0-9]+$`; anything else throws `ArgumentException`.

- [ ] **Step 1: Write failing tests.** `FakeHandler` records each `HttpRequestMessage` and its body, and returns a canned response.

```csharp
// ClaudeCredentialsTests
[Fact] public void Read_ReturnsTokenOrgAndExpiry() { /* write both files into a temp claudeHome */ }
[Fact] public void Read_MissingCredentialsFile_ThrowsAuthMissing() { }
[Fact] public void Read_RereadsFileEachCall() { /* change token on disk between calls -> new token */ }

// CloudSessionClientTests
[Fact] public async Task Create_SendsHeadersAndBodyShape()
{ /* Authorization "Bearer tok", x-organization-uuid, anthropic-beta "ccr-byoc-2025-07-29";
     body.events[0].type == "event"; body.events[0].data.type == "user";
     body.events[0].data.message.content == "hello"; body.session_context.model == "claude-sonnet-5-5";
     body.session_context.sources[0].url == "https://github.com/servicetitan/titan-lens-ossie";
     body.tags contains "fyrd" */ }
[Fact] public async Task Create_ParsesSessionId() { /* canned {"id":"session_01abc",...} */ }
[Fact] public async Task Get_ParsesStatusAndPostTurnSummary()
{ /* canned body in the design §11 shape: session_status "idle",
     external_metadata.post_turn_summary.status_category "completed" */ }
[Fact] public async Task ListEvents_PassesCursorAndExtractsAssistantText()
{ /* after_id in query; assistant content [{type:"text",text:"FYRD-OK"}] -> Text "FYRD-OK";
     env_manager_log -> Text null; has_more / first_id / last_id mapped */ }
[Fact] public async Task SendMessage_PostsUnwrappedUserEvent() { /* body.events[0].type == "user" */ }
[Fact] public async Task Status401_ThrowsAuthException_WithoutToken() { /* message lacks "tok" */ }
[Fact] public async Task Status500_ThrowsApiException_WithStatus() { }
[Fact] public async Task Get_RejectsPathInjection() { /* "session_1/../x" -> ArgumentException, no request sent */ }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test runtime/Fyrd.sln --filter "CloudSessionClientTests|ClaudeCredentialsTests"`
Expected: compile errors.

- [ ] **Step 3: Implement `ClaudeCredentials`, `CloudSessionModels`, and `CloudSessionClient`.** Build request JSON with `System.Text.Json.Nodes` (`JsonObject`) so the code reads like the shapes above. Parse responses with `JsonDocument`, and keep `Raw` as a cloned `JsonElement`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test runtime/Fyrd.sln`
Expected: all pass (Tasks 1–4 and 7).

- [ ] **Step 5: Commit**

```bash
git add runtime
git commit -m "feat(core): CloudSessionClient for Claude Code cloud sessions"
```

---

### Task 8: Cloud-session spike (throwaway)

This is a spike: no tests, and it doesn't ship. Its output is a findings document that Plan 2 is written from.

**It runs real cloud sessions on the owner's subscription and pushes `fyrd-spike/*` branches to `servicetitan/titan-lens-ossie`.** Confirm the branch names with the owner before running it.

**The owner runs it, not the implementing agent.** Claude Code's auto-mode safety check blocks an agent from reading the claude.ai credentials. The implementer writes the spike. The owner runs it with `! dotnet run --project spikes/cloud-sessions -- <question>` and pastes the output.

**Files:**
- Create: `spikes/cloud-sessions/CloudSessionsSpike.csproj` (console, `net10.0`, references `runtime/src/Fyrd.Core`) and `spikes/cloud-sessions/Program.cs`. Each question is a subcommand (`q1` … `q7`) that logs every response as one JSON line to `spikes/cloud-sessions/out/<question>.jsonl` (gitignored) and prints a short summary.
- Create: `docs/superpowers/spikes/2026-10-29-cloud-sessions.md`

**What the spike must answer.** Each question becomes a section in the findings doc, with response excerpts.

1. **The lead has the peer tools.** Create a session with `CloudSessionClient.CreateAsync` and ask it to list its `mcp__claude-code-remote__*` tools. Does a session created through the API have `create_session`, `send_message`, `get_session`, and `list_events`?
2. **The lead spawns tagged peers.**
   - The lead creates 2 implementer sessions with `create_session`, using:
     - tags `fyrd`, `fyrd-spec-spike`, `fyrd-role-implementer`, and `fyrd-unit-a` or `fyrd-unit-b`
     - `source_url` = `https://github.com/servicetitan/titan-lens-ossie`
     - `outcome_branch` = `fyrd-spike/2026-10-29-a` or `-b`
     - `permission_mode` = `acceptEdits`
   - Each implementer makes a trivial commit and pushes it.
   - Then the lead merges both into `fyrd-spike/2026-10-29-integration` and pushes that.
   - Record the child session IDs, whether the pushes landed, and how long it all took.
3. **Discovery by tag.** Find a list-sessions endpoint: look in the CLI binary for `GET /v1/sessions` with a `tags` query, and record the exact request. Confirm Fyrd can list `fyrd-spec-spike` sessions without stored IDs.
4. **Owner question round trip.**
   - The lead ends a turn with a fenced `fyrd-question` JSON block.
   - Fyrd reads it from the last assistant event and from `post_turn_summary.needs_action`.
   - **Wait at least 2 hours** with the spike process stopped, then answer with `SendMessageAsync`.
   - Did the session resume with its context intact? Was the environment reprovisioned? Check for `env_manager_log` events.
5. **Status fields while working.** Poll `GetAsync` every 10 s during question 2. Record every distinct value seen for `session_status`, `connection_status`, and `post_turn_summary.status_category`.
6. **Interrupt and archive.** Find the interrupt and archive endpoints in the CLI binary (`/v1/sessions/{id}/...`), call them on one child, and record the exact requests and responses.
7. **Token refresh.** Check `expiresAt` in the credentials file, run `claude auth status`, then check `expiresAt` again. Does running a `claude` command refresh an expired token?

- [ ] **Step 1: Confirm with the owner** the branch names above, and that question 4 leaves a session idle for 2+ hours.
- [ ] **Step 2: Write `Program.cs`** with subcommands `q1`–`q7`, using `CloudSessionClient` from Task 7. Where a question needs an endpoint the client doesn't have (list, interrupt, archive), add a raw `HttpClient` call in the spike only, using the request found in the CLI binary.
- [ ] **Step 3: The owner runs `q1`, `q2`, `q3`, `q5`, `q6`, and `q7`,** and pastes the output. Record the results.
- [ ] **Step 4: The owner runs `q4`** (a 2+ hour wait, with the process stopped in between). Record the results.
- [ ] **Step 5: Write the findings doc.** For each question, give the answer (yes / no / partly), the evidence, and what Plan 2 should do because of it. End with a "Changes to the design" list, empty if there are none.
- [ ] **Step 6: Clean up** after confirming with the owner: archive the spike sessions, delete the remote `fyrd-spike/*` branches, then commit.

```bash
git add spikes/cloud-sessions/*.cs spikes/cloud-sessions/*.csproj spikes/cloud-cli docs/superpowers/spikes .gitignore
git commit -m "spike: verify cloud-session assumptions for orchestration"
```
