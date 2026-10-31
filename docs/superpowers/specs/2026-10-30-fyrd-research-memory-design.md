# Fyrd: Research, Memory, and Hardening (Plan 3 design)

Date: 2026-10-30
Status: Draft for owner review. Drafted by Claude at the owner's request ("draft it yourself"), so every choice here is Claude's call unless it is marked as the owner's.
Parent design: `2026-10-29-fyrd-design.md` (§3 step 2, §6, §9, §10). Builds on Plans 1–2 as merged (`3fc836e`).

## 1. Intent

Fill the parts of the original design that Plan 2 deferred:

1. **Research stage** (parent §3 step 2, §9). Researcher peons investigate a rough spec independently and help the owner refine it. It is the one deliberately interactive stage, and the owner finalizes the spec.
2. **Owner-proxy persona and visible memory** (parent §6, §9, §10). A persona file captures how the owner decides. It grows from remembered decisions, the owner can see and edit it, and it reaches every peon, not only the lead.
3. **Hardening from Plan 2's reviews and smoke run:**
   - Spec IDs are never reused.
   - Recovery when the server dies mid-launch.
   - Retrying an answer that failed to reach the lead.
   - Ignoring archived sessions on the Board.

## 2. Goals, non-goals, anti-goals

**Goals**
- From a Draft spec, the owner clicks **Research**. Researchers investigate, their questions arrive in the Inbox, and the owner gets back a **proposed revised spec** they can accept, edit, or discard before attacking.
- Remembered decisions and the owner-proxy persona are visible and editable in the console, and are injected into every lead and every peer.
- No duplicate or stale sessions show up on the Board after a spec is deleted and recreated, or after a crash.

**Non-goals (Plan 3)**
- Handoff card, PR creation, and post-PR cleanup (Plan 4).
- Cross-repo specs (later).
- Automatically rewriting the owner-proxy persona with an LLM. It grows by appending remembered decisions; the owner edits the prose.

**Anti-goals**
- Research turning into a chat. Research must give back **one** proposed spec plus only the questions that genuinely need the owner, not a stream of messages.
- Research ever pushing code or creating branches.
- Memory silently changing what the owner wrote.

## 3. Research stage

### Flow
1. **Start.** A spec in `Draft` shows a **Research** button next to **Attack**.
2. **Claim and launch.** Research does Draft → `Researching` atomically, the same way Attack does, then launches a **research lead** cloud session:
   - permission mode `auto`
   - tags `fyrd`, `fyrd-spec-<id>`, `fyrd-role-research-lead`
   - prompt from a new persona `personas/research-lead.md`, with the spec, memory and owner-proxy text injected
3. **Researchers.** The research lead creates **3 researcher peers** with the `researcher` persona and tags `fyrd-role-researcher` and `fyrd-unit-<angle>`. Each takes a distinct angle:
   - `codebase`: how the repo works today, and where the change lands
   - `approach`: options and prior art, ending with a recommendation
   - `risks`: edge cases, failure modes, test strategy

   Researchers read and run things but never push, never create branches, and never open PRs.
4. **Reporting.** Researchers report to the research lead with `send_message`, as Plan 2 peers do. The research lead asks the owner only what it can't settle, using the existing `fyrd-question` protocol, so questions arrive in the Inbox with free text allowed.
5. **The proposal.** The research lead finishes with a new block, **`fyrd-spec-draft`**: `{"markdown":"<full revised spec>","summary":"<what changed and why, ≤10 lines>","openQuestions":["…"]}`. It then archives its researchers and ends.
6. **Back to Draft with a proposal.** Fyrd stores the proposal as `specs/<id>/proposal.md` plus `proposal.json` (summary, openQuestions, the source session), and the spec returns to `Draft` with `hasProposal: true`. On failure (`fyrd-failed`), the spec returns to `Draft` with `error` set.
7. **Review.** The spec view shows the **current spec** and the **proposal** side by side, plus the summary. The owner then does one of these:
   - **Accept:** the proposal becomes `spec.md`, and the old one is kept as `spec.v<N>.md`.
   - **Edit then accept:** edit the proposal in a textarea, then accept.
   - **Discard:** delete the proposal.

   Research can run again on the updated spec.
8. **Attack is unchanged.** It is blocked only while a spec is `Researching`, or while it has an unreviewed proposal. The owner must accept or discard it first, so a proposal is never silently ignored.

### Why a research lead instead of Fyrd launching researchers directly
It reuses the Plan 2 machinery that the smoke run proved: lead → peers → `send_message` wake-ups → `fyrd-question` → relay. That keeps all coordination in the cloud (parent §9: runs without the owner's machine), and gives one place to merge three researchers' findings into a single proposal.

### Board
In `Researching`, the spec's units are the three angles, at stage `Research`. `UnitStage` gains `Research`, and the console shows it as one marker.

## 4. Owner-proxy persona and memory

### Files (hand-editable)
- **`~/.fyrd/data/memory/owner-proxy.md`:** owner-written prose saying how they decide, such as risk appetite, test expectations and style rules. If missing, it is created with a short template. Fyrd never rewrites it.
- **`~/.fyrd/data/memory/global.md` and `<owner>__<repo>.md`:** remembered decisions, as Plan 1 built them. Fyrd only appends.

### Growth
"Grows from remembered decisions" (parent §9) means every remembered answer is appended to the decision files, as it is today. The proxy prose stays the owner's. *(Claude's call: no LLM summarization, per the anti-goal about silently changing what the owner wrote.)*

### Delivery
- **Lead and research-lead prompts** include the owner-proxy text and the remembered decisions, in their own delimited blocks.
- **Peers:** the lead persona is told to put a fixed `<owner-preferences>` block, a copy of what it was given, into every peer's `append_system_prompt`. Peers settle small choices themselves and escalate only through the lead.

### Console
A new **Memory** tab, alongside Inbox and Board:
- The owner-proxy prose, editable, with Save.
- Global decisions and per-repo decisions, each as an editable textarea with Save. Saving overwrites the file, because it is the owner's edit.
- A note that changes apply to specs launched from now on.

## 5. Hardening

1. **Unique spec IDs.** New IDs are `yyyyMMdd-<slug>-<4 hex>`. Existing IDs stay valid (`SpecIds.IsValid` accepts both forms). This stops a recreated spec from picking up a deleted spec's tagged sessions.
2. **Recovering a launch interrupted by a crash.** On startup, and every poll pass, a spec that has been `Queued` or `Researching` for more than 2 minutes with no `SessionId` is reconciled:
   - Fyrd lists sessions tagged `fyrd-spec-<id>` with role `lead` or `research-lead`.
   - If one exists, Fyrd adopts it with `SetLead` and the matching status.
   - Otherwise, Fyrd reverts the spec to `Draft` with `error: "Launch interrupted; try again"`.
3. **Relay retry.** A card's answer records `relayStatus`: `pending`, `sent` or `failed`.
   - The answer endpoint sets `pending`, tries once, and records the result.
   - Each poll pass retries `pending` and `failed` relays for active specs, up to 5 attempts, then leaves the card `failed`.
   - The Board shows "Answer not delivered: retry" for `failed`, and Retry resets the attempts.
4. **Archived sessions.** Unit stage and state, and the Board's peer list, ignore sessions whose status is `archived`.
5. **The attention-card gap from Plan 2's final review.** The "last turn had a question" flag resets when Fyrd relays an answer, so a later permission prompt still produces an attention card.

## 6. Constraints carried from the parent design

- **Permission mode:** all leads are created with `auto` (the owner's decision). Research peers are `auto` too and are told never to push.
- **Undocumented API:** stays inside `CloudSessionClient`; the token is never logged; state stays hand-editable; no new packages.
- **Squad cap:** applies to build squads only. Research always uses 3 researchers.

## 7. Left to the builder

- Exact console layout of the side-by-side spec/proposal view, as long as it's readable at a 1280 px width and renders text, not HTML.
- The prose of `research-lead.md` and `researcher.md`, within the rules above. It mirrors the tone and structure of the Plan 2 personas, including the waiting model and the safety rules.
- The owner-proxy template text.
