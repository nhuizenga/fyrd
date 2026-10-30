# Fyrd — Design

Date: 2026-10-29
Status: Approved 2026-10-29; revised 2026-10-29 (peons moved from Managed Agents to Claude Code cloud sessions, §5, §10, §11)

## 1. Intent

Fyrd builds software with an army of specialized agents ("peons"), each owning one part of the software development lifecycle. The owner manages the army from a browser console that asks only for decisions the army truly cannot make itself, via one-click responses.

### Why

The owner's current workflow fails in four ways:

1. **Context loss.** Swapping between many agent sessions, the owner must remember what each agent and they themselves were doing every time they return.
2. **No visibility.** Subagent state and status are opaque.
3. **Local overload.** Every session runs locally and the machine bogs down.
4. **Chattiness.** With no structured way for agents to check each other's work, that checking lands on the owner as conversation.

### What success looks like

The owner writes a spec, tells the fyrd to attack it, walks away for an hour, and returns to a short list of decisions that genuinely required them — then later to a PR.

## 2. Goals, non-goals, anti-goals

### Goals (v1)

- One repo, spec → PR, end to end.
- A browser console with two tabs: **Inbox** (default) and **Board**.
- Peons run in the cloud (Claude Code cloud sessions, billed to the owner's Claude subscription), not on the owner's machine.
- Peons check each other's work (review + validation) so the owner doesn't.
- Owner decisions are remembered by default and not asked again.
- Started from Claude Code with `/fyrd:up`.
- If Fyrd itself crashes, work is not lost and sessions remain reachable by hand.

### Non-goals (v1, planned for later)

- **Cross-repo specs** and **deployment-ordered PR holding** (see §8). The design must not preclude them.
- **PR babysitting** after opening (watching CI/comments, dispatching fix squads). v1 stops at opening the PR.
- **AKS-hosted peons** (self-hosted sandboxes). The design must not preclude them.
- Mobile / phone access.
- Multi-user / team use.

### Anti-goals (would make Fyrd a failure even if it technically works)

- Fyrd is **as chatty** as the owner's current process.
- The owner still feels **overwhelmed keeping up with many sessions**.
- The Inbox contains decisions the fyrd could have made itself with its personas, memory, and peers.
- Fyrd ever merges a PR. The owner merges.

## 3. The pipeline (happy path)

1. **Spec.** Owner writes a small spec from their understanding of the problem.
2. **Research.** A group of researcher peons independently investigate and help the owner refine the spec. This is the one deliberately interactive stage. The owner finalizes the spec.
3. **Breakdown.** A **lead** peon takes the final spec and breaks it into parallelizable units of work. The lead persists for the whole run as the team's decision-maker and reviewer.
4. **Build.** Each unit gets a **squad**: implementer, reviewer, validator (1:1:1).
   - The implementer reports to the lead when a decision is needed or a plan change must be communicated.
   - The reviewer reviews every unit; the validator tests every unit.
   - The lead escalates to the owner's Inbox **only** what it cannot settle with personas, memory, and peers.
5. **Final review.** The lead reviews the integrated result.
6. **Handoff.** The lead tells the owner the work is done, points to where the running code can be seen, and asks whether to open a PR (one click).
7. **PR.** On approval, Fyrd opens the PR.
8. **Merge.** The owner merges when satisfied. (Post-PR babysitting: see §9.)

## 4. The console

A browser tab served by the local runtime.

### Inbox tab (default view)

- Shows only decisions the owner must make.
- Each card: which spec/unit it came from, one clear question, one-click answers, the lead's recommendation and its reason, and a "Details" affordance for full context.
- Cards carry enough summary that the owner never has to reconstruct context.
- Answering a card records the decision to memory by default (see §6).
- Research-stage interaction (refining the spec with researchers) also surfaces here.

### Board tab (survey view)

- Every unit across every active spec, with each squad's stage (implement / review / validate) and status (running, done, waiting on owner, blocked).
- Overall progress per spec.
- Clicking a unit shows that squad's recent activity — observation without having to intervene.
- Grouped by spec; when cross-repo arrives, units show their repo.

Approved mockup direction: "Inbox | Board" tabs, inbox first (see `.superpowers/brainstorm/` mockups, local only).

## 5. Architecture (decided)

### Repo shape

This repo is a **Claude Code plugin that ships its own runtime**.

- `/fyrd:up` starts the backend and console locally and opens the browser tab.
- The local runtime is the console plus an observer/relay; agent work and lead-to-squad coordination happen in the cloud (see §9, §10).

### Backend

- **C#**, plain `HttpClient`. No Anthropic SDK (see §11 for why).
- Peons run as **Claude Code cloud sessions** (the sessions at claude.ai/code), driven through the API Claude Code itself uses (§11). Reason: owner wants subscription billing, not Claude Platform API billing, and wants every peon visible in Claude desktop.
- Every peon is its own cloud session: one lead per spec, plus one session per researcher, implementer, reviewer and validator.
- The lead creates and steers its peers with the `claude-code-remote` tools that cloud sessions already have (`create_session`, `send_message`, `get_session`, `list_events`, `interrupt_session`, `archive_session`). This is what keeps a spec running while the owner's machine is off (§9).
- The backend polls session status and events to drive the Board and to route escalations to the Inbox. It also creates the lead session when the owner attacks a spec, and sends owner answers back into it.
- Later move to AKS: cloud sessions accept a self-hosted environment ID (`ccpool_...`) in the same `environment_id` field. Nothing else changes.

### Frontend

- **React**, served by the local backend.

### State

- Local files under `~/.fyrd/data/`, human-readable and hand-editable (specs, plans, unit status, decision memory, session IDs).
- On `/fyrd:up`, the runtime resumes from this state, then finds the spec's sessions by tag (§11) and catches up on their events.

### Crash recovery

- Cloud sessions run on Anthropic's infrastructure and survive a Fyrd crash.
- Every peon session is visible in Claude desktop and at claude.ai/code. The owner can open any of them and steer it by hand, or pull one into a terminal with `claude --teleport <id>`.
- Session titles and tags name the spec, unit and role, so the owner can tell sessions apart without Fyrd.

### Considered and rejected

- **Claude Managed Agents.** Fully documented and has a C# SDK, but bills through a Claude Platform API key, and sessions appear only in the Console, not Claude desktop.
- **Driving cloud sessions through the `claude` CLI only.** Creating needs a pseudo-terminal, and reading back needs `claude -p --teleport`, which runs a model turn per read and needs a local clone. Superseded by the API in §11.
- **Remote Control.** Sessions run on the machine that starts them, which brings back the local overload problem.
- **Claude Agent SDK on own infrastructure.** No C# SDK, and sessions don't appear in Claude desktop.

## 6. Memory and personas

- **Decisions are remembered by default.** Once the owner answers a question (canonical example: "What kind of test coverage do you expect?"), Fyrd must not ask it again.
- Memory is part of the hand-editable state in `~/.fyrd/data/`.
- Peons use tailored personas plus memory to settle questions before escalating.

## 7. Constraints

- Minimal dependencies. (Owner rejected an existing SDLC plugin, Forge, partly for having too many baked-in dependencies, and because its process doesn't match this pipeline.)
- Fyrd must be easy to run and to monitor work across any number of repos simultaneously (multi-repo concurrency is required by design even though v1 specs are single-repo).
- Owner's repos span radically different tech stacks.

## 8. Later: cross-repo work

Recorded now so v1 doesn't preclude it.

- Owner's real repos: `titan-lens`, `data-platform-data-model`, `reporting-embedded`. Different stacks, different deploy cadences.
- A change is often co-developed in two repos in parallel but has **strict deployment ordering**.
- Fyrd **holds downstream PRs** until the upstream change is deployed.
- Deployment detection: all repos deploy via **GitHub Actions**; the upstream change counts as deployed once a successful deploy workflow run includes the upstream commit.

## 9. Owner answers (builder check)

- **Handoff / "running code":** the lead produces a branch the owner checks out and runs locally.
- **Running unattended:** once attacked, a spec must be able to run to completion without the owner's machine, unless an issue is found that needs the owner. Implication: coordination between the lead and squads must happen server-side, not in the local backend (see §10, first item). The local runtime is the console and an observer; if it is offline, the fyrd keeps working and the console catches up on `/fyrd:up`.
- **Spec authoring:** the owner writes a `.md` file or writes directly in the console. The attack is started from the console. There is no `/fyrd:attack` command.
- **Research stage:** researchers' questions appear in the Inbox; answers can be free text as well as one-click.
- **Personas:** both kinds.
  - Role personas per peon type (researcher, lead, implementer, reviewer, validator), shipped with Fyrd.
  - An owner-proxy persona ("how the owner would decide") that answers on the owner's behalf. It grows from remembered decisions and the owner can edit it.
- **v1 ends at opening the PR.** No CI/comment watching in v1.
- **Squad cap:** a limit on concurrent squads, adjustable from the console UI.

- **Sandbox access:** confirmed. The target repo's code may be cloned into Anthropic-hosted sandboxes, and its tests run without internal company systems. Self-hosted sandboxes stay a later goal.
- **v1 target repo:** `servicetitan/titan-lens-ossie`.

## 10. Builder's calls

(Choices made by the designer/builder rather than the owner; listed for owner review.)

- **Server-side coordination.** To satisfy "continues without the owner's machine", each spec's lead is a cloud session that creates and steers its peer sessions with the `claude-code-remote` tools. The local backend only creates the lead, polls sessions for the console, and relays owner answers. To verify in the Plan 1 spike: that the lead has those tools when created through the API, and that a lead can wait hours on an owner answer.
- **One session per peon** (not subagents inside one session). Each peon gets its own status, its own container and branch (no shared-filesystem collisions), and its own row in Claude desktop.
- **Integration.** Each unit works on its own branch; the lead merges unit branches into one integration branch for the spec, and assigns a squad to resolve conflicts. The handoff branch is that integration branch; the PR is opened from it.
- **Squad cap default:** 4, range 1–8, adjustable in the console. The lead reads the current cap from Fyrd before starting each squad (Fyrd includes it in every message it sends the lead).
- **Usage visibility instead of cost.** Subscription billing has no per-session dollar figure. The Board shows the account's rate-limit status (`rate_limit_info`) and each session's context usage instead.
- **Escalation protocol.** A peon asks the lead with `send_message`. When the lead needs the owner, it ends its turn with a fenced `fyrd-question` JSON block (question, options, recommendation, reason, unit). Fyrd detects the lead is idle, parses the block from the last assistant event, and makes an Inbox card. The owner's answer goes back into the lead as a user message.
- **Session tags** (set at creation; used for discovery): `fyrd`, `fyrd-spec-<specId>`, `fyrd-role-<role>`, and `fyrd-unit-<unitId>` where it applies.
- **Memory delivery.** Remembered decisions and the owner-proxy persona go into each session's `append_system_prompt` at creation.
- Remembered decisions default to the scope of the repo they came from; they can be widened and are viewable/editable.
- Plugin also provides `/fyrd:down` and `/fyrd:status`.
- Researchers take distinct angles (e.g. codebase, prior art, risks).

## 11. Cloud session API (undocumented)

Fyrd drives cloud sessions through the HTTP API that the `claude` CLI uses. It is **not a public API**. The shapes below come from the CLI binary (v2.1.312) and were verified on 2026-10-29 with two probe sessions (`spikes/cloud-cli/`).

### Auth

- `Authorization: Bearer <claude.ai OAuth access token>`, read from `~/.claude/.credentials.json` (`claudeAiOauth.accessToken`), where Claude Code keeps it.
- `x-organization-uuid: <uuid>`, from `~/.claude.json` (`oauthAccount.organizationUuid`).
- `anthropic-version: 2023-06-01`, `anthropic-beta: ccr-byoc-2025-07-29`.
- Fyrd reads the credentials file on every request and never stores or logs the token. When the token is expired or rejected, Fyrd shows a banner telling the owner to run any `claude` command (which refreshes it) or `claude auth login`.

### Endpoints (verified)

| Purpose | Call | Notes |
|---|---|---|
| Create | `POST /v1/sessions` | Body: `title`, `tags`, `environment_id`, `events` (first user message wrapped as `{"type":"event","data":{...}}`), `session_context` (`sources` (git repo URL + revision), `outcomes`, `environment_variables`, `model`, `append_system_prompt`). Returns `id` (`session_...`). |
| Status | `GET /v1/sessions/{id}` | `session_status` (`idle`, `working`, ...), `connection_status`, `external_metadata.post_turn_summary` (`status_category`, `needs_action`, `recent_action`, `status_detail`), `context_usage`, `rate_limit_info`, `current_branches`. |
| Transcript | `GET /v1/sessions/{id}/events?limit=&after_id=&before_id=` | Paged events: `user`, `assistant`, `result`, `env_manager_log`, and more, each with `uuid` and `created_at`. |
| Send | `POST /v1/sessions/{id}/events` | Body `{"events":[<user event>]}`; a user event is `{uuid, session_id:"", type:"user", parent_tool_use_id:null, message:{role:"user", content}}`. |

To verify in the Plan 1 spike: listing sessions filtered by tag, interrupting, archiving, and whether a session created through the API has the `claude-code-remote` tools.

### Risks the owner accepted

1. **Breakage.** Anthropic can change this API in any Claude Code release. All calls live in one class (`CloudSessionClient`) so a break is a single-file fix, and the CLI path (`claude --cloud` / `claude -p --cloud`) stays a known fallback for create and send.
2. **Terms.** Fyrd calls Anthropic with the owner's claude.ai login outside Claude Code. The Agent SDK docs restrict third-party products from using claude.ai login; personal use on the owner's own account is a gray area. The owner checks this with their company's Anthropic contact.
