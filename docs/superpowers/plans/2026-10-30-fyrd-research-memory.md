# Fyrd Research, Memory and Hardening (Plan 3 of 4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clicking Research on a Draft spec launches a research lead with 3 researcher peers, routes their questions to the Inbox, and returns one proposed revised spec that the owner accepts, edits or discards. Remembered decisions and an owner-proxy persona become visible, editable, and delivered to every peon. Plan 2's known gaps are closed: reused spec IDs, launches interrupted by a crash, undelivered answers, archived sessions, and the attention-card flag.

**Architecture:**
- **Research** reuses Plan 2's machinery. The research lead is a lead session with its own persona and role tag. Researchers are its peers. A new `fyrd-spec-draft` block carries the proposal, which `SpecRunner` stores beside the spec.
- **Memory** gains an owner-proxy file and a read/write API with a Memory tab. The lead persona copies the owner preferences into every peer's system prompt.
- **Hardening** lives in the stores and the runner.

**Tech Stack:** unchanged (.NET 10, ASP.NET Core, xUnit; React/Vite/Vitest; Node launcher). No new packages.

**Spec:** `docs/superpowers/specs/2026-10-30-fyrd-research-memory-design.md`. Parent: `docs/superpowers/specs/2026-10-29-fyrd-design.md`. Also read `docs/superpowers/plans/2026-10-30-fyrd-orchestration.md` (Plan 2 protocol and conventions) and `docs/superpowers/spikes/2026-10-30-plan2-smoke.md`.

## Global Constraints

- **Permission mode:** every lead and research lead is created with `auto`.
- **Research peers never push, never create branches, never open PRs.**
- **Research:** exactly 3 researchers, with unit IDs and angles `codebase`, `approach`, `risks`. The squad cap applies only to build squads.
- **New tags:** `fyrd-role-research-lead`, `fyrd-role-researcher`. Existing tags are unchanged.
- **Protocol addition:** fenced `fyrd-spec-draft` with body `{"markdown":"<full revised spec>","summary":"<≤10 lines>","openQuestions":["…"]}`. All other blocks are unchanged.
- **New spec IDs:** `yyyyMMdd-<slug>-<4 lowercase hex>`. Old IDs (without the suffix) stay valid.
- **Memory files:** `memory/owner-proxy.md` is owner-written and never rewritten by Fyrd except on an explicit save from the Memory tab. The decision files are append-only from answers, and overwritten only on an explicit save from the Memory tab.
- **Relay retry:** at most 5 attempts per answer; statuses `pending`, `sent`, `failed`.
- **Interrupted launch:** a spec `Queued` or `Researching` with no `SessionId` for more than 2 minutes is reconciled.
- The token never appears in logs or responses; all undocumented-API shapes stay in `CloudSessionClient`; state stays hand-editable; a malformed file never takes down the console.

## Review Focus

1. **The research lead emits `fyrd-spec-draft` with an empty or missing `markdown`.** Expected: no proposal is stored; the spec goes back to `Draft` with `error` "Research returned no spec"; nothing crashes. Test: Task 5 `Poll_SpecDraftWithoutMarkdown_SetsError`.
2. **The owner accepts a proposal while research is still running** (a stale tab, or two tabs). Expected: accept is rejected with 409 unless the spec is `Draft` with a proposal. Test: Task 4 `AcceptProposal_WhileResearching_Returns409`.
3. **A memory save carries huge or binary-looking text.** Expected: saves larger than 200 KB are rejected with 400; normal text round-trips exactly, including CRLF and unicode. Test: Task 3 `PutMemory_TooLarge_Returns400`, `PutMemory_RoundTripsExactly`.
4. **The server restarts while a relay is pending.** Expected: the next poll pass retries it, so the answer is delivered once and not twice. Test: Task 2 `Poll_RetriesPendingRelay_Once`.
5. **An old ID spec and a new suffixed-ID spec coexist** (upgrading from Plan 2 data). Expected: both list, attack, and poll. Test: Task 1 `List_OldAndNewIdFormats`.

---

### Task 1: Spec IDs, Research stage enum, archived sessions, attention flag

**Files:** modify `runtime/src/Fyrd.Core/Specs/SpecIds.cs`, `Specs/SpecModels.cs`, `Orchestration/SpecRunner.cs`, `Orchestration/RunnerState.cs`, `Orchestration/LeadRelay.cs`, `runtime/src/Fyrd.Server/Endpoints/BoardEndpoints.cs`; tests in `SpecStoreTests`, `SpecRunnerTests`, `ApiTests`.

**Interfaces:**
- `SpecIds.NewId(string title, DateTimeOffset now)` returns `yyyyMMdd-<slug>-<4 hex>`. The hex comes from `RandomNumberGenerator`. The slug is capped so the whole ID still matches `IsValid`.
- `IsValid` accepts `^[0-9]{8}-[a-z0-9-]{1,48}\z`, which already covers both forms. Keep that regex and test that it does.
- `UnitStage` gains `Research`, after `Done`. Its JSON value is `"Research"`.
- `SpecRunner` and the Board peer list skip sessions whose `SessionStatus == "archived"`.
- `RunnerState.ClearQuestionFlag(string specId)`: `LeadRelay.RelayAnswerAsync` calls it after a successful send, so a later permission prompt can create an attention card. The flag moves from `SpecRunner` into `RunnerState`, so the relay and the runner share it.

- [ ] **Step 1: Write failing tests:** `NewId_HasFourHexSuffix`, `NewId_SameTitleTwice_Differs`, `List_OldAndNewIdFormats`, `Poll_ArchivedPeerIgnoredForStage`, `Board_ArchivedPeerNotListed`, `Poll_AttentionCardAfterAnsweredQuestion` (question → answer relayed → a lead idle with `needs_action` produces an attention card).
- [ ] **Step 2:** Run the tests and confirm they fail. Then implement. Then run `dotnet test runtime/Fyrd.sln` and confirm all pass.
- [ ] **Step 3: Commit** with the message `fix: unique spec ids, research stage, ignore archived sessions, reset question flag on relay`.

---

### Task 2: Relay retry and interrupted-launch recovery

**Files:** modify `Inbox/InboxModels.cs`, `Inbox/InboxStore.cs`, `Orchestration/LeadRelay.cs`, `Orchestration/SpecRunner.cs`, `Specs/SpecStore.cs`, `Endpoints/InboxEndpoints.cs`; tests in `InboxStoreTests`, `LeadRelayTests`, `SpecRunnerTests`, `ApiTests`.

**Interfaces:**
- `CardAnswer` gains these trailing fields: `string? RelayStatus = null`, `int RelayAttempts = 0`, `string? RelayError = null`.
- `InboxStore`:
  - `InboxCard SetRelay(string cardId, string status, string? error)` increments `RelayAttempts` when `status` is `failed`.
  - `IReadOnlyList<InboxCard> ListRelaysToRetry()` returns answered cards with a `SourceKey`, `RelayStatus` in {`pending`, `failed`}, and `RelayAttempts` < 5.
  - `InboxCard ResetRelay(string cardId)` sets status `pending` and attempts 0.
- **Answer endpoint:** after `Answer`, it calls `SetRelay(pending)`, attempts the relay, then sets `sent`, or `failed` with the message. The response still carries `relayError` on failure.
- **New route** `POST /api/inbox/{id}/relay` calls `ResetRelay` and then relays once. It returns the card, or 404 or 409 if the card is unanswered.
- **`SpecRunner.PollOnceAsync`**, after spec passes:
  - retries `ListRelaysToRetry()` through `LeadRelay`;
  - reconciles stale launches: `SpecStore.ListStaleLaunches(TimeSpan olderThan)` returns `Queued`/`Researching` runs with no `SessionId` whose `UpdatedAt` is older than the threshold. `SpecRun` gains a trailing `DateTimeOffset? UpdatedAt = null`, set on every mutate.
  - For each stale launch, it runs `ListByTagAsync("fyrd-spec-<id>")`. A non-archived session with role `lead` → `SetLead`. Nothing found → `Finish(id, Draft, null, "Launch interrupted; try again")`. Task 4 extends this so role `research-lead` → `SetResearchLead`. Task 4 also adds `Reconcile_AdoptsExistingResearchLead` to its tests.
- `SpecRunner` gets `LeadRelay` injected. Update its registration in `Program.cs`.

- [ ] **Step 1: Write failing tests:** `SetRelay_FailedIncrementsAttempts`, `ListRelaysToRetry_StopsAtFive`, `Poll_RetriesPendingRelay_Once` (after it is sent once it isn't resent), `RelayRoute_ResetsAndSends`, `Reconcile_AdoptsExistingLead`, `Reconcile_NoSession_RevertsToDraftWithError`, `Reconcile_RecentLaunch_Untouched`.
- [ ] **Step 2:** Run the tests and confirm they fail. Then implement. Then run the full suite and confirm all pass.
- [ ] **Step 3: Commit** with the message `feat: retry undelivered answers and recover interrupted launches`.

---

### Task 3: Owner-proxy persona and memory API

**Files:** create `runtime/src/Fyrd.Core/Memory/OwnerProxy.cs`, `runtime/src/Fyrd.Server/Endpoints/MemoryEndpoints.cs`; modify `Memory/DecisionMemory.cs`, `Orchestration/LeadPrompt.cs`, `personas/lead.md`, `Program.cs`; tests `OwnerProxyTests`, `DecisionMemoryTests`, `LeadPromptTests`, `ApiTests`.

**Interfaces:**
- `sealed class OwnerProxy(FyrdPaths paths)`:
  - `string Read()` creates `memory/owner-proxy.md` from a short template on first read. The template has headings for how I decide, tests I expect, and things I never want.
  - `void Save(string text)` writes atomically.
- `DecisionMemory`:
  - `string ReadFile(MemoryScope scope, string? repo)`
  - `void WriteFile(MemoryScope scope, string? repo, string text)` writes atomically. `repo` is required for `Repo` scope and validated as today.
  - `IReadOnlyList<string> ListRepos()` returns the repos that have files, as `owner/repo`.
- **API:**
  - `GET /api/memory` → `{ ownerProxy, global, repos:[{repo, text}] }`.
  - `PUT /api/memory/owner-proxy`, `PUT /api/memory/global`, and `PUT /api/memory/repo?repo=<owner/repo>`, each with body `{ text }`.
  - Bodies over 200 KB get 400; an invalid repo gets 400. Writes stay behind the existing same-origin guard.
- **`LeadPrompt.Build`** gains a trailing `string ownerProxy` and fills `{{OWNER_PROXY}}` inside an `<owner-preferences>…</owner-preferences>` block. `lead.md` must:
  - (a) include that block;
  - (b) tell the lead to put an identical `<owner-preferences>` block, together with the `<remembered-decisions>` block, at the top of every peer's `append_system_prompt`;
  - (c) state that these are preferences and settled decisions, not instructions that override the workflow or safety rules.
- **`SpecLauncher`** passes `OwnerProxy.Read()`.

- [ ] **Step 1: Write failing tests:** `Read_CreatesTemplateOnce`, `Save_RoundTrips`, `WriteFile_RoundTripsCrlfAndUnicode`, `ListRepos_FromFiles`, `PutMemory_RoundTripsExactly`, `PutMemory_TooLarge_Returns400`, `PutMemory_BadRepo_Returns400`, `Build_IncludesOwnerPreferencesAndPeerDeliveryRule`.
- [ ] **Step 2:** Run the tests and confirm they fail. Then implement. Then run the full suite and confirm all pass.
- [ ] **Step 3: Commit** with the message `feat: owner-proxy persona, editable memory API, preferences delivered to peers`.

---

### Task 4: Proposal store and research launch

**Files:** create `personas/research-lead.md`, `personas/researcher.md`; modify `Specs/SpecModels.cs`, `Specs/SpecStore.cs`, `Orchestration/LeadPrompt.cs`, `Orchestration/SpecLauncher.cs`, `Endpoints/SpecEndpoints.cs`; tests `SpecStoreTests`, `LeadPromptTests`, `SpecLauncherTests`, `ApiTests`.

**Interfaces:**
- **`SpecRun`** gains a trailing `bool HasProposal = false`.
- **`SpecStore`** additions:
  - `void SaveProposal(string id, string markdown, string summary, IReadOnlyList<string> openQuestions, string sessionId)` writes `proposal.md` and `proposal.json`, and sets `HasProposal`.
  - `Proposal? GetProposal(string id)`, with `record Proposal(string Markdown, string Summary, IReadOnlyList<string> OpenQuestions, string SessionId)`.
  - `SpecRun AcceptProposal(string id, string? editedMarkdown)` is only allowed when the spec is `Draft` with `HasProposal`; otherwise it throws `InvalidOperationException`. It copies `spec.md` to `spec.v<N>.md` (next free N), writes the edited or proposed markdown to `spec.md`, deletes the proposal, and clears `HasProposal` and `Error`.
  - `SpecRun DiscardProposal(string id)`
  - `SpecRun SetResearchLead(string id, string sessionId)` sets `SessionId`, status `Researching`, and clears `LastEventId`.
  - `SpecRun UpdateMarkdown(string id, string markdown)` works in `Draft` only.
- **`Personas`** gains `ResearchLead` and `Researcher`, both loaded from `personas/`.
- **`LeadPrompt.BuildResearch(SpecRun, specMarkdown, memory, ownerProxy, FyrdConfig, Personas)`** fills `research-lead.md`. Its placeholders are those of `lead.md` minus the squad cap, plus `{{RESEARCHER_PERSONA}}`.
- **`research-lead.md`** must:
  - start with `get_session` (no ID) to learn its own ID;
  - create exactly 3 researchers with unit IDs `codebase`, `approach` and `risks`, using `source_url` `https://github.com/{{REPO}}`, permission mode `auto`, peer model, tags (`fyrd`, `fyrd-spec-<id>`, `fyrd-role-researcher`, `fyrd-unit-<angle>`), no `outcome_branch`, and `append_system_prompt` = researcher persona plus `<owner-preferences>` and `<remembered-decisions>`;
  - wait to be woken by peer messages;
  - ask the owner only through `fyrd-question`;
  - merge the findings into one revised spec;
  - finish with exactly one `fyrd-spec-draft` (or `fyrd-failed`);
  - archive its researchers;
  - follow these safety rules: never push, never create branches, never open PRs, never touch sessions it didn't create.
- **`researcher.md`:** read and run the repo to investigate its angle, never push and never create branches, report findings to the lead's session ID with `send_message` and then end its turn, never emit `fyrd-*` blocks.
- **`SpecLauncher.LaunchResearchAsync(string specId, CancellationToken ct)`** works like `LaunchAsync`, with role tag `fyrd-role-research-lead` and title `"Fyrd research · <title>"`. It calls `SetResearchLead`, and reverts to `Draft` on failure.
- **New endpoints:**
  - `POST /api/specs/{id}/research`: 409 on a missing env, missing personas, `HasProposal`, or a failed `TryTransition(Draft→Researching)`; 502 cases as for attack.
  - `GET /api/specs/{id}/proposal`: 404 if there is none.
  - `POST /api/specs/{id}/proposal/accept` with body `{ markdown? }`: 409 if not allowed.
  - `POST /api/specs/{id}/proposal/discard`
  - `PUT /api/specs/{id}/markdown` with body `{ markdown }`: Draft only, else 409; empty gets 400.
- **Attack** returns 409 "Review the research proposal first" when `HasProposal`.

- [ ] **Step 1: Write failing tests:**
  - `SaveAndGetProposal`
  - `AcceptProposal_VersionsOldSpec`
  - `AcceptProposal_WithEdit_UsesEdit`
  - `AcceptProposal_WhileResearching_Returns409` (API)
  - `Discard_ClearsFlag`
  - `Research_LaunchesResearchLeadWithAutoAndTags`
  - `Research_WithProposal_Returns409`
  - `Attack_WithProposal_Returns409`
  - `PutMarkdown_DraftOnly`
  - `BuildResearch_ContainsThreeAnglesSafetyAndSpecDraftProtocol`
  - `ShippedPersonas_LoadResearchFiles`
- [ ] **Step 2:** Run the tests and confirm they fail. Then implement. Then run the full suite and confirm all pass.
- [ ] **Step 3: Commit** with the message `feat: research launch, proposal store, research personas`.

---

### Task 5: Runner handles research

**Files:** modify `Orchestration/Protocol.cs`, `Orchestration/SpecRunner.cs`, `Specs/SpecStore.cs` (`ListActive` includes `Researching`); tests `ProtocolTests`, `SpecRunnerTests`.

**Interfaces:**
- **`Protocol.Parse`** recognizes `fyrd-spec-draft` and returns `ParsedBlocks.SpecDraft` as `SpecDraftDecl(string Markdown, string? Summary, IReadOnlyList<string> OpenQuestions)`. If `markdown` is missing or blank, it adds a warning and returns `SpecDraft = null`, but marks `SpecDraftInvalid = true`.
- **`SpecRunner`**, for a spec in `Researching`:
  - `SpecDraft` → `SaveProposal(...)` then `Finish(id, Draft, null, null)`.
  - `SpecDraftInvalid` → `Finish(id, Draft, null, "Research returned no spec")`.
  - `Failed` → `Finish(id, Draft, null, reason)`.
  - Questions → cards, exactly as for build.
  - Units come from researcher peers (`fyrd-role-researcher`), with stage `Research` and state as for build peers. Done when the spec leaves `Researching`.
  - A research lead's `fyrd-done` or `fyrd-units` is ignored with a warning.
- **Build specs** behave as before, except that a `fyrd-spec-draft` from a build lead is ignored with a warning.

- [ ] **Step 1: Write failing tests:** `Parse_SpecDraft`, `Parse_SpecDraftWithoutMarkdown_Flagged`, `Poll_SpecDraft_SavesProposalAndReturnsToDraft`, `Poll_SpecDraftWithoutMarkdown_SetsError`, `Poll_ResearchFailed_ReturnsToDraftWithReason`, `Poll_ResearcherPeers_StageResearch`, `Poll_BuildLeadSpecDraft_Ignored`.
- [ ] **Step 2:** Run the tests and confirm they fail. Then implement. Then run the full suite and confirm all pass.
- [ ] **Step 3: Commit** with the message `feat: poll research leads into proposals`.

---

### Task 6: Console: research, proposal review, memory, relay retry

**Files:** create `console/src/specs/ProposalReview.tsx`, `console/src/specs/SpecEditor.tsx`, `console/src/memory/MemoryTab.tsx` and their tests; modify `console/src/{App.tsx, api.ts, types.ts}`, `board/BoardTab.tsx`, `inbox/InboxCard.tsx`, the `App.css` stage marker.

**Behavior:**
- **Tabs:** Inbox (default), Board, Memory.
- **Board, per spec:**
  - `Draft` shows **Research** and **Attack**. Attack is disabled with the hint "Review the research proposal first" when `hasProposal`.
  - `Researching` shows the 3 angle units, each with the `Research` marker `◆`.
  - `hasProposal` shows a **Review proposal** button, which opens `ProposalReview`.
  - **Edit spec** (Draft only) opens `SpecEditor`.
  - Server 409/502 details show inline.
- **`ProposalReview`:**
  - The current spec on the left and the proposal on the right, both as plain text in scrollable panes.
  - The summary and the open questions above them.
  - **Accept**, **Edit then accept** (the right pane becomes a textarea), and **Discard**.
- **`MemoryTab`:**
  - Owner preferences, Global decisions, and one section per repo, each a textarea with Save.
  - It shows the 400 detail on a failed save.
  - The note "Changes apply to specs launched from now on."
- **`InboxCard`:** an answered card with `relayStatus` `failed` is shown in an "Answers not delivered" strip at the top of the Inbox, with **Retry** (`POST /api/inbox/{id}/relay`). The Inbox list itself shows only open cards, as today.
- **`GET /api/inbox/undelivered`** (server, add here): answered cards with `relayStatus` `failed`.
- Everything renders as text, with no HTML injection. No new npm packages.

- [ ] **Step 1: Write failing tests** (Vitest and ApiTests):
  - research button calls the API, and attack is disabled with a proposal
  - proposal review: accept, edit-accept and discard each call the right API
  - memory tab saves and shows a 400
  - the undelivered strip retries
  - the `◆` marker for Research
  - `GetUndelivered_ListsFailedRelays`
- [ ] **Step 2:** Run the tests and confirm they fail. Then implement. Then run all three suites plus `npm run build`, and confirm they pass.
- [ ] **Step 3: Commit** with the message `feat(console): research, proposal review, memory tab, undelivered answers`.

---

### Task 7: Owner smoke run (manual, real cloud)

This uses the owner's claude.ai login and the real cloud. Agents don't run it.

- [ ] **Step 1:** Run `/fyrd:up`. Write a rough spec on `servicetitan/titan-lens-ossie`, for example "Document how to run the tests locally", and click **Research**.
- [ ] **Step 2:** The Board shows `Researching` with 3 angle units. Answer any cards.
- [ ] **Step 3:** The spec returns to `Draft` with **Review proposal**. Review it, edit if wanted, accept, and confirm that `spec.v1.md` holds the original.
- [ ] **Step 4:** On the Memory tab, add one line to Owner preferences. Attack the spec, and confirm (in a peer's claude.ai session) that its system prompt starts with `<owner-preferences>`.
- [ ] **Step 5:** Record the results in `docs/superpowers/spikes/2026-10-30-plan3-smoke.md`. Confirm that no branch was pushed during research. Delete the build branches after confirming with the owner.
