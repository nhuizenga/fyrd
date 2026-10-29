# Fyrd — Design

Date: 2026-10-29
Status: Draft (builder check done; pending owner approval)

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
- Peons run in the cloud (Claude Managed Agents), not on the owner's machine.
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

- **C#**, using the official **Anthropic C# SDK**.
- Peons run as **Claude Managed Agents** sessions (Anthropic-hosted loop + per-session sandbox). Peon roles are persisted Agent definitions; each squad member / researcher / lead is a Session.
- The backend streams session events to drive the Board and to route escalations to the Inbox.
- Must be structured so the peon execution substrate can later move to AKS (Managed Agents self-hosted sandboxes, or equivalent) without a rewrite.

### Frontend

- **React**, served by the local backend.

### State

- Local files under `~/.fyrd/data/`, human-readable and hand-editable (specs, plans, unit status, decision memory, session IDs).
- On `/fyrd:up`, the runtime resumes from this state, reattaching to live sessions by stored ID.

### Crash recovery

- Managed Agents sessions run server-side and survive a Fyrd crash.
- The owner can see them at platform.claude.com/sessions and steer one by hand with `ant beta:sessions connect <session-id>`.
- (Considered and rejected: Claude Code cloud sessions, which appear in Claude desktop but expose no documented way for a program to read status/output; Remote Control, which runs locally and reintroduces the overload problem.)

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

- **Server-side coordination.** To satisfy "continues without the owner's machine", each spec runs as a Managed Agents lead session that coordinates its squads server-side (Managed Agents multiagent support). The local backend only starts runs, streams events for the console, and relays owner answers. Must be verified during planning: that sub-agent activity is visible enough in the event stream to drive the Board, and that a lead can wait on an owner answer without losing state.
- **Integration.** Each unit works on its own branch; the lead merges unit branches into one integration branch for the spec, and assigns a squad to resolve conflicts. The handoff branch is that integration branch; the PR is opened from it.
- **Squad cap default:** 4, adjustable in the console.
- **Cost visibility:** a running cost estimate per spec on the Board.
- Remembered decisions default to the scope of the repo they came from; they can be widened and are viewable/editable.
- Plugin also provides `/fyrd:down` and `/fyrd:status`.
- Researchers take distinct angles (e.g. codebase, prior art, risks).
