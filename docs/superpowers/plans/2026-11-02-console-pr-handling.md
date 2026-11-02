# Console PR handling: implementation plan

Date: 2026-11-02 · Repo: `nhuizenga/fyrd` · Base branch: `main`

## Goal

Make pull requests in the Fyrd console accurate, live and actionable. You should be able to see every session's PR state at a glance, act on a PR without leaving the console (merge, nudge the Validator → Reviewer flow, fix failing checks), and merge stacked PRs safely in the right order.

## What exists today

| Area | Today | Where |
|---|---|---|
| PR ownership | A session owns the PRs whose head is any of its branches (per-repo `gh pr list --state=all --json url,headRefName`, cached 2 min). The code also reads PR URLs from `session_context.outcomes[].git_info`, but the cloud sessions API never fills them (#12), so that path is dead. One PR shows on every session on that branch (Builder, Validator, Reviewer). | `runtime/src/Fyrd.Core/Hub/OwnedPullRequests.cs`, `HubRunner.cs:93,277,353` |
| PR status | `gh pr view --json number,title,state,isDraft,statusCheckRollup`, collapsed to `state`, `draft` and `checks` (none, pending, passing or failing). `CanMerge` = open, not a draft, and checks passing or none. | `Hub/GitHub.cs` |
| Endpoints | `GET /sessions/{id}/pull-requests` (live, per PR), `POST …/ready`, `POST …/merge` (re-checks `CanMerge` on the server). | `Fyrd.Server/Endpoints/HubEndpoints.cs:164-196,548` |
| Merge | `gh pr merge --merge` (a merge commit). | `GitHub.cs` `MergeAsync` |
| Session row | Repo chip, branch chip, then `#N` (one PR) or `N PRs`. No state. | `console/src/screens/SessionsScreen.tsx:348-354` |
| Details pane | PR chip (title only in the tooltip). Badges: `Draft`, state when not open, and `Checks x` only when open and not a draft. Icon buttons: open on GitHub, mark ready (drafts), merge (when `canMerge`), with an inline confirm alert. | `SessionsScreen.tsx:611-671` |
| Freshness | Statuses are fetched when the selected session or its PR list changes, and after an action. Never refreshed otherwise. | `SessionsScreen.tsx:133-147` |
| Validator / Reviewer flow | Runs on agent messages only: Builder opens a draft and sends `Validation request:`, the Validator marks the PR ready and sends `Review request:`, the Controller starts a Reviewer. The owner can send `PR #{n} is ready` or `Watch #{pr} …` by typing. | `personas/hub/{builder,validator,reviewer,controller}.md` |

## Gaps and friction

0. **The backend shells out to `gh`.** That needs the CLI installed and signed in, takes one process per call with a 30 s timeout, and returns errors as stderr text (`GhCli` in `GitHub.cs`).
1. **Wrong merge method.** The console merges with a merge commit, but shared memory says squash, and recent `main` history is squashed. Repos that disallow merge commits fail with a raw `gh` error (a 502).
2. **Rows are blind.** The design spec (§12.2) and mockup call for PR state and check counts on each row. Rows show only `#N`, so you must click every session to find a failing or mergeable PR.
3. **Stale status.** The details pane never refreshes, so checks go pending → passing without the Merge button appearing.
4. **Checks hidden on drafts.** Builders work in draft and the Validator gates on CI, but the checks badge is suppressed for drafts. There are no counts and no link to the failing check.
5. **No review, conflict or behind state.** Fyrd doesn't read `reviewDecision`, `latestReviews`, `mergeable` or `mergeStateStatus`. Merge is offered on a conflicting PR and fails on GitHub. Fyrd Reviewers post `COMMENT` reviews, so "has been reviewed" is invisible.
6. **Stacks are invisible.** Fyrd never reads `baseRefName`, so it can't tell you that #12 is stacked on #11, or that #12 should merge first (shared memory: merge from the top of the stack down). You can merge out of order.
7. **No nudges.** You can't ask for validation, review, a PR Monitor, or a fix for failing checks without typing a free-form message to the right session.
8. **Minor issues.** The PR title is shown only in a tooltip. Action errors land in the page-level alert, far from the PR. The stale comment on `MergeAsync` says "a merge commit, matching how this repo's history is merged".

## Decisions (settled with the owner)

| # | Decision | Choice |
|---|---|---|
| D1 | Merge method | **Always squash** (Octokit `MergeMethod = Squash`, Phase 0). No method menu. |
| D2 | Status source for live rows | **Widen the per-repo listing** that `OwnedPullRequests` already makes (one call per repo per TTL) to return status fields, and publish statuses in hub state. The details pane still re-reads live on select and after an action. |
| D3 | Cross-session PR view (a new tab or Inbox cards) | **Neither for now.** Enrich session rows and the details pane only. |
| D4 | Stacked PRs | **Show the stack, guard merges, and add a "Merge stack" action.** |
| D5 | Between stack steps | **Wait for checks.** A server-side job merges the top PR, polls the next PR until it can merge (timeout 30 min), merges it, and repeats. It stops and reports on a failure, conflict, close or timeout. |
| D6 | Owner nudge buttons | **All four:** Request validation, Request review, Watch PR (to the Controller), and Fix failing checks (to the owning Builder). |
| D8 | Which PRs a session owns | **All PRs whose head is one of the session's branches, plus every open PR stacked with them** (parents and children, followed all the way along the chain). Stacked PRs are **fully owned**: they get the same status and actions as the session's own PRs. Remove the dead outcomes path. |
| D7 | How the backend talks to GitHub | **Directly, through a library, never by running `gh`.** Use **Octokit.NET** for REST (squash merge, signed-in user, rate limits) and **GitHub GraphQL posted with `HttpClient`** for the status listing and mark-ready, which REST can't do. The token comes from the `GH_TOKEN` environment variable (falling back to `GITHUB_TOKEN`), set before `/fyrd:up`. |

## Prioritised improvements

Ordered by value ÷ effort. Phase 0 goes first because every later phase reads GitHub through it. Each phase is one stacked PR (shared memory: stacked branches and PRs), and each PR bumps `.claude-plugin/plugin.json` once (`CLAUDE.md`).

| Phase | Improvement | Value | Effort |
|---|---|---|---|
| 0 | Replace `gh` with Octokit and GraphQL, using a `GH_TOKEN` token; show GitHub sign-in status | High (it's the base for the other phases) | M |
| 1 | Richer status model, and a details pane that shows checks on drafts, counts, failing-check link, review, conflicts/behind, base and title | High | S–M |
| 2 | Live statuses in hub state, state chips on session rows, auto-refreshing details, "also on" sessions | High | M |
| 3 | Nudge buttons, plus Controller routing rows | Medium–High | S–M |
| 4a | Stack detection, stack display and merge-order guard | Medium | S–M |
| 4b | "Merge stack" background job | Medium | M–L |

---

## Phase 0: Talk to GitHub directly (Octokit and GraphQL)

**Branch:** `fyrd/github-api` from `main`.

### Why both

GitHub's REST API has no single check rollup: you'd need combined statuses plus check runs, two or more calls per PR. It also can't mark a PR ready for review, which is GraphQL-only (`markPullRequestReadyForReview`). One GraphQL query per repo returns every field the console needs. Octokit (official and maintained) covers the REST side: squash merge, the signed-in user and rate limits.

### Token

- At startup, read `GH_TOKEN`, else `GITHUB_TOKEN`, from the environment. `scripts/fyrd.mjs` already spawns the server with the inherited environment, so nothing needs passing through.
- The token lives in memory only. It never goes into hub state, logs, error messages or the console.
- The scopes it needs are covered by a classic token with `repo`. With a fine-grained token, grant **Pull requests: read/write**, **Contents: read/write** (merge), **Commit statuses: read** and **Checks: read** on the repos Fyrd works in.
- The server reads the token once. Changing it needs `/fyrd:down` and then `/fyrd:up`, because `/fyrd:up` reuses a running server.
- For owners who already use `gh`, the README tip is: `export GH_TOKEN=$(gh auth token)` before `/fyrd:up`.

### Server

1. Add the `Octokit` NuGet package to `Fyrd.Core.csproj`, pinned to the latest stable version.
2. Add `Hub/GitHubApi.cs`, `GitHubApi : IGitHub`, replacing `GhCli`:
   - Construct it with `(GitHubToken token, HttpClient http, IGitHubClient octokit)`. Register `HttpClient` through `AddHttpClient("github")` with base `https://api.github.com/`, `User-Agent: fyrd` and `Authorization: Bearer <token>`. Build Octokit's `GitHubClient` with `new ProductHeaderValue("fyrd")` and `new Credentials(token)`.
   - **Repo listing:** one GraphQL request per repo with two aliases:
     - `all: pullRequests(first:100, orderBy:{field:UPDATED_AT, direction:DESC})` with `url number title state isDraft headRefName baseRefName`
     - `open: pullRequests(first:50, states:OPEN)` with `id url reviewDecision mergeable mergeStateStatus latestReviews(first:10){nodes{state}} commits(last:1){nodes{commit{statusCheckRollup{state contexts(first:100){nodes{__typename ... on CheckRun{name status conclusion detailsUrl} ... on StatusContext{context state targetUrl}}}}}}}`
   - **`GetAsync(url)`:** the same fields for `repository(owner,name){pullRequest(number)}`, including the node `id`.
   - **`MarkReadyAsync`:** a GraphQL mutation `markPullRequestReadyForReview(input:{pullRequestId})`, using the id from a fresh `GetAsync`.
   - **`MergeAsync`:** Octokit `PullRequest.Merge(owner, repo, n, new MergePullRequest { MergeMethod = PullRequestMergeMethod.Squash })`. This lands the D1 squash default here, so Phase 1 needn't repeat it.
   - Keep the query strings as `const` text in one place. Parse the responses with `System.Text.Json`, keeping today's rollup rules (`FAILURE`, `TIMED_OUT`, `CANCELLED`, … mean failing; any running check means pending).
3. Errors:
   - Map Octokit `ApiException`, GraphQL `errors[]` and HTTP failures to `GitHubException`, with a short owner-facing message: "GitHub refused the merge: <message>", or "GitHub token lacks access to <repo>".
   - Add `GitHubRateLimitException(resetAt)`, raised on 403/429 with `x-ratelimit-remaining: 0` or a GraphQL `RATE_LIMITED`. `OwnedPullRequests` keeps its last good listing and doesn't retry a repo before `resetAt`.
   - With no token, `GitHubApi` throws `GitHubNotConfiguredException` without any network call.
4. Sign-in status:
   - On startup, and then every 10 minutes, call Octokit `User.Current()`.
   - Publish `gitHub: { configured: bool, login: string | null, error: string | null }` in hub state. It holds no token, only the login.
5. Delete the dead outcomes path: `HubRunner.PullRequests` and its fixture in `HubRunnerTests.cs:608`. Ownership then comes only from the listing (D8, Phase 2).
6. `Program.cs`: register `IGitHub` as `GitHubApi`. Delete `GhCli`, its `ProcessStartInfo` code and its tests. Keep the `PullRequestUrl` and `GitHubRepo` validators: the URL is still parsed into owner, repo and number, and nothing but those three reaches a query.
7. `scripts/fyrd.mjs up`: when neither `GH_TOKEN` nor `GITHUB_TOKEN` is set, print one line: `GH_TOKEN is not set: PR status and PR actions are off. Set it and run /fyrd:down then /fyrd:up.` Don't fail. `status` adds `GitHub: signed in as <login>` or `GitHub: not configured`, taken from `/api/hub/state`.
8. Docs: add `GH_TOKEN` and `GITHUB_TOKEN` to the README's environment variables (with scopes, the restart note and the `gh auth token` tip), and add a line to `commands/up.md`.

### Console

9. `types.ts` gets `HubState.gitHub`. When `configured` is false, the details pane's Pull requests section shows a note instead of statuses: "Set GH_TOKEN before /fyrd:up to see PR status and act on PRs." Ready and merge are hidden. When `error` is set (a bad or expired token), show it the same way. **Without a token, no session shows any PRs**, because every PR comes from the listing. Rows show no PR chips, and the note says why.

### Tests

- `runtime/tests/Fyrd.Core.Tests/GitHubApiTests.cs`, using the existing `FakeHandler`:
  - GraphQL listing parse from JSON fixtures (mixed rollup, a draft, conflicting, merged)
  - `GetAsync` parse
  - the mark-ready mutation body and the id taken from a fresh read
  - squash merge: Octokit built over `new HttpClientAdapter(() => fakeHandler)`; assert `PUT …/pulls/{n}/merge` with `merge_method: "squash"`
  - GraphQL `errors[]` becomes a `GitHubException`, a rate-limit response becomes `GitHubRateLimitException`, no token means no request
  - the token never appears in exception messages
- `OwnedPullRequestsTests`: no retries before `resetAt`.
- `HubRunnerTests`: the outcomes fixture is gone, and PRs come only from the listing.
- `HubApiTests`: `gitHub` in state, PR actions return 503 "GitHub is not configured" with no token.
- `scripts/lib.test.mjs`: the missing-token warning text.
- Console: the not-configured note, and the buttons hidden.

---

## Phase 1: Richer PR status

**Branch:** `fyrd/pr-status-model`, stacked on Phase 0.

### Server

1. `Hub/GitHub.cs` / `GitHubApi.cs`
   - Extend `PullRequestStatus`. Keep the existing positional fields and add init-only properties so existing callers and tests still compile:
     - `Base` (`baseRefName`), `Head` (`headRefName`)
     - `ChecksPassed`, `ChecksFailed`, `ChecksPending` (ints, counted in the same loop that builds the rollup)
     - `FailingCheck`: `{ Name, Url }` for the first failing check (`name`/`context` plus `detailsUrl`/`targetUrl`), or null
     - `Review`: `none | commented | approved | changes_requested`. Use `reviewDecision` when it is set. Otherwise use `commented` if `latestReviews` has any entry, else `none`.
     - `Mergeable`: `mergeable | conflicting | unknown` (from `mergeable`)
     - `MergeState`: lower-cased `mergeStateStatus` (`clean`, `behind`, `blocked`, `dirty`, `unstable`, `draft`, `has_hooks` or `unknown`)
   - `CanMerge` = `State == "open" && !Draft && Checks is "passing" or "none" && Mergeable != "conflicting"`. Don't gate on `blocked`, because GitHub's own branch rules answer that, and the error is surfaced.
   - Fill them from the GraphQL fields Phase 0 already requests: one `ParseStatus(url, JsonElement)` serves both `GetAsync` and the open-PR alias of the listing.
2. `Endpoints/HubEndpoints.cs`: the `GET …/pull-requests` projection adds the new fields. The merge 409 message mentions conflicts.

### Console

3. `types.ts`: add the fields to `PullRequestStatus`. Type `review`, `mergeable` and `mergeState` as string unions, and `failingCheck` as `{ name: string; url: string | null } | null`.
4. `SessionsScreen.tsx` details pane. Extract a `PullRequestItem` component (`console/src/components/PullRequestItem.tsx`) so Phases 2 to 4 have one place to grow:
   - The first line is `#N` plus the title, truncated, with the full title as a tooltip.
   - The second line has badges:
     - state (when not open)
     - `Draft`
     - checks with counts in the mockup style (`✓ 8  ✗ 1  ◌ 2`), shown for drafts too
     - `Conflicts` (danger), `Behind base` (warning)
     - review: `Reviewed`, `Approved` or `Changes requested`
     - `→ <base>`
   - A failing-checks badge links to `failingCheck.url` in a new tab.
   - Action errors show inline under the PR item, not only in the page-level alert.
5. Merge confirm text: "This squash-merges #N into `<base>` on GitHub."

### Tests

- `runtime/tests/Fyrd.Core.Tests/GitHubApiTests.cs`:
  - Parse fixtures for a mixed rollup with counts and the failing check's URL, `reviewDecision` set vs. only `COMMENT` reviews, and `mergeable: CONFLICTING`.
  - `CanMerge` is false when the PR is conflicting.
- `runtime/tests/Fyrd.Server.Tests/HubApiTests.cs`: the projection includes the new fields, and merge on a conflicting PR returns 409.
- `console/src/screens/SessionsScreen.test.tsx`: checks show on drafts with counts, the conflicts badge shows and Merge is hidden, the base badge and title render.
- Update `console/e2e/mockApi.ts` fixtures.

---

## Phase 2: Live statuses in hub state and on rows

**Branch:** `fyrd/pr-live-status`, stacked on Phase 1.

### Server

1. `IGitHub.ListAsync` returns full statuses (rename the record to `PullRequestSummary` or extend `PullRequestHead`). They come from Phase 0's single GraphQL request per repo: the light `all` alias joined with the heavy `open` alias by URL. Only open PRs carry the rollup, which keeps the query's point cost down.
2. **Ownership rule (D8).** This is a pure function in `Hub/PullRequestStacks.cs`, reused by Phase 4a/4b and mirrored in `console/src/stacks.ts`.
   - **Direct:** every PR in the repo listing, of any state, whose `head` is one of the session's branches. This matches today's rule.
   - **Stacked:** walk from each *open* directly owned PR, following links between *open* PRs in the same repo only:
     - **Up:** the open PR whose `head` equals this PR's `base`.
     - **Down:** every open PR whose `base` equals this PR's `head`.
     - Repeat until nothing new is found, and add everything reached.
   - Never link through the repo's default branch (add `defaultBranchRef { name }` to the Phase 0 query). PRs that merely target `main` are not stacked with each other.
   - Ignore merged and closed PRs when walking, so a reused branch name can't pull in old history. Ignore cross-repo heads (`isCrossRepository`).
   - The result stays one list of URLs (`HubSessionView.PullRequests`), so `PrAction`'s rule "only this session's PRs" now covers stacked PRs too. Add a `StackedPullRequests` subset (URLs owned only through a stack) so the console can mark them.
3. `OwnedPullRequests`:
   - Keep the per-repo cache and TTL, and lower the TTL to **60 s**. That is one GraphQL request per repo per minute.
   - Add `Invalidate(repo)`, called after any console PR action so the next poll re-lists.
   - `FindAsync` keeps returning URLs. Add `Status(url)` to read the cached summary.
4. `HubState` / `HubRunner`:
   - Publish a top-level `pullRequests: Record<url, PullRequestStatus>` in the hub state, so a PR is held once even when several sessions own it.
5. `PrAction` calls `ownedPrs.Invalidate(repo)` and `state.RequestPoll()` after a successful ready or merge.

### Console

6. Session rows (`row()` in `SessionsScreen.tsx`):
   - With one PR, show a compact status chip: `#N` plus a state icon/colour (draft grey, open blue, merged purple, closed muted), a checks dot (red failing, amber pending, green passing), and a conflict marker.
   - With several PRs, show `N PRs` with the worst status (failing > conflicts > pending > passing) and a tooltip listing each.
   - Use Anvil2 `Chip` colours/status. Keep chips truncating like `.chip-branch`.
7. Details pane:
   - Stacked PRs are listed with the session's own PRs, with the same badges and actions, plus a small "stacked" tag (from `stackedPullRequests`).
   - Render from `state.pullRequests` immediately.
   - Still call `api.pullRequests(id)` on select and after actions, and **re-fetch every 30 s while a session is selected and any of its PRs is open with pending checks**. That way Merge appears soon after CI goes green, without waiting for the TTL.
8. **"Also on":** under each PR item, list the other hub sessions whose `pullRequests` include this URL (directly or through a stack), with role, status icon and a link (for example `validator-3 · idle`, `reviewer-2 · running`). This is the light substitute for the pipeline view that D3 deferred.

### Tests

- `PullRequestStacksTests.cs`, for the ownership rule:
  - a direct PR only
  - a parent and a child are added
  - a three-deep chain is followed both ways
  - a fork adds both children
  - a merged parent is not followed
  - two PRs that both target `main` are not linked
  - other repos are never added
  - cross-repo heads are ignored
  - `StackedPullRequests` holds the indirect ones only
- `HubApiTests.cs`: merge and ready are accepted on a stacked PR. Merge is still rejected on a PR outside the stack.
- `OwnedPullRequestsTests.cs`: joining the two aliases, TTL 60 s, `Invalidate`, and a failed open-listing that keeps the last good statuses.
- `HubRunnerTests.cs`: the `pullRequests` map is in hub state and deduplicated across sessions.
- `HubApiTests.cs`: an action invalidates the cache and requests a poll.
- `SessionsScreen.test.tsx`: the row chip shows the worst status, the 30 s refresh uses fake timers, and "also on" lists the sibling sessions.

---

## Phase 3: Nudge buttons

**Branch:** `fyrd/pr-nudges`, stacked on Phase 2.

The buttons send ordinary owner messages through the existing `POST /api/hub/sessions/{id}/message` (`api.sendMessage`), which goes through the owner's `claude` CLI so sessions read them as the owner's (#20). No new endpoint is needed.

| Button | Shown when | Sent to | Text |
|---|---|---|---|
| Request validation | open | Controller | `Validate PR <url>. Base branch: <base>. Builder: <title> (<id>).` |
| Request review | open, not a draft | Controller | `PR #<n> is ready: <url>` (already routed) |
| Watch PR | open | Controller | `Watch <url> and fix anything that comes up` (already routed) |
| Fix failing checks | `checks == failing` or conflicting | owning Builder | `Checks are failing on <url>: <failing check names>. Fix them, run the tests, push, and reply as before.` For conflicts: `<url> has merge conflicts with <base>. Merge <base> in (no force-push), fix, run the tests, push, and reply as before.` |

1. The **owning Builder** is the session with role `builder` whose `pullRequests` include the URL. If there are several, prefer the one whose `branch` equals the PR's `head`. If there is none, hide the button.
2. When there's no Controller (`state.controller == null`), the Controller buttons are disabled, with the tooltip "No Controller".
3. Put the buttons in an overflow menu on the PR item (Anvil2 `Menu`/`Popover`), next to the existing icon buttons, so the pane stays narrow. "Watch PR" starts a paid session, so it gets a one-line confirm ("Start a PR Monitor for #N?"). The others send immediately.
4. Show a "Sent to <session>" toast or inline note. Errors show inline (Phase 1 pattern).
5. **Personas:** add one routing row to `personas/hub/controller.md`:
   - `A message "Validate PR <url>" (from the owner)` → `Start a Validator for that PR as for a validation request.`

   Make the existing `PR #{n} is ready` row accept a URL too. Seeded personas show `updateAvailable` in Settings, so the owner must accept the persona update (call this out in the PR body).
6. `skills/controller/fyrd-peasant/SKILL.md`: check that the "Validation request" path works without a `Builder:` line and without a `Spec:` line. When they are absent, the Controller looks up the Builder by branch.

### Tests

- `SessionsScreen.test.tsx`: each button's visibility rules, the exact message text sent to the right session id (the Controller vs. the owning Builder), disabled with no Controller, and the Watch confirm.
- `HubPersonasTests` / seed hash tests still pass after the persona edit (update the seed hash where tests pin it).

---

## Phase 4a: Stack display and merge-order guard

**Branch:** `fyrd/pr-stacks`, stacked on Phase 3.

1. **Stack model:** reuse `PullRequestStacks` from Phase 2 and its console twin.
   - Over the session's open PRs (which by D8 already include the whole stack), PR **B is stacked on A** when `B.base == A.head`.
   - A *chain* runs from the root (a base that isn't another of these PRs' heads) to the top.
   - When a PR has more than one open child, the stack has a *fork*. Show it, but don't allow a stack merge.
2. **Details pane:**
   - Show `Stacked on #A` and `#B stacked on this`. They are in the same session's list, so these link to the PR item, and "also on" names the other sessions that own it directly.
   - Show a compact chain line: `main ← #11 ← #12 ← #13 (top)`.
3. **Guard:** when a PR has an open child, the Merge confirm becomes a warning: "#12 is stacked on this PR. Merge from the top of the stack down: merge #13, then #12, then this. Merging this first leaves #12 targeting a merged branch." Offer **Merge stack** (Phase 4b) and **Merge this only anyway**. Enforce the same rule on the server: `POST …/merge` returns 409 with that text unless the body has `force: true`, and the console sends `force: true` only from "Merge this only anyway".
4. Rows: a small stack glyph on the PR chip when the PR is part of a chain.

### Tests

- `stacks.test.ts`: chain order, root and top, fork detection.
- The UI guard text and `force` flow, and the server 409 without `force`.

---

## Phase 4b: "Merge stack" job

**Branch:** `fyrd/pr-merge-stack`, stacked on Phase 4a.

### Behaviour

- Shown on any PR in a linear chain (no fork) of two or more open PRs.
- It merges the **whole chain, top-down, to the root**: squash the top into its base (the branch below), then the next, and so on until the root is squashed into its base.
- **Preconditions,** checked on the server with live `GetAsync` for every PR:
  - all PRs are open, not drafts and not conflicting
  - the top PR `CanMerge`
  - no PR's checks are failing (pending is allowed, because it is waited on)
  - every PR in the chain is in the selected session's `PullRequests`. By D8 that is true for any chain the session is part of, so this only rejects a stale or crafted request.
- **Loop,** for each PR from top to root:
  - Poll `GetAsync` every 20 s until `CanMerge`, then squash-merge.
  - Stop with `failed` when the checks fail, the PR is conflicting, closed or merged by someone else, the base changed unexpectedly, or the PR has waited 30 min.
  - After each merge, `Invalidate(repo)` and `RequestPoll()`.
- **One job per chain.** A second request for any PR in a running chain returns 409. The owner can cancel. Cancelling stops before the next merge and never undoes merges already done.

### Server

1. `Hub/StackMerge.cs`:
   - `StackMergeJobs`: a singleton holding `ConcurrentDictionary<jobId, StackMergeJob>`, with `IGitHub`, `TimeProvider`, `OwnedPullRequests` and `HubState`.
   - The job record: `{ Id, Repo, Prs (top→root), Index, Status: waiting|merging|done|failed|cancelled, Message, StartedAt, UpdatedAt }`.
   - Run it on `Task.Run` with a token linked to `IHostApplicationLifetime.ApplicationStopping`. Use `TimeProvider` delays so tests drive it with `FakeTimeProvider`.
2. Endpoints:
   - `POST /sessions/{id}/pull-requests/merge-stack { url }` → 202 `{ jobId }`.
   - `POST /stack-merges/{jobId}/cancel` → 204.
   - Publish jobs in hub state as `stackMerges` (drop finished jobs 10 minutes after they end).
3. Jobs live **in memory only.** A server restart drops a running job, leaving some PRs merged and the rest open. Re-clicking recomputes the chain from what is still open, so it resumes safely. Document this in the README.

### Console

4. A "Merge stack" item in the PR menu, with a confirm listing the order (`#13 → #12 → #11 into main, squash, waiting for checks between steps`).
5. While a job runs, each PR item in the chain shows a step line (`Waiting for checks on #12 (step 2 of 3)`, then `Merged`, `Failed: <reason>`) and a Cancel button. Rows show a spinner glyph on chips in a running job.

### Tests

- `StackMergeTests.cs` with `FakeGitHub` and `FakeTimeProvider`:
  - happy path for three PRs (merge order asserted)
  - waits through pending checks
  - stops on failing checks, conflicts, an externally merged PR and a timeout
  - cancel between steps
  - a second job on the same chain is rejected
  - a PR outside the session's PRs is rejected
- `FakeGitHub` gets scripted status sequences per URL (a queue of statuses returned on successive `GetAsync` calls).
- `HubApiTests.cs`: endpoints, 202/409, and `stackMerges` in state.
- `SessionsScreen.test.tsx`: confirm text order, progress lines and Cancel.
- e2e: a mock stack in `mockApi.ts` and a smoke test that the chain line and menu item render.

---

## How to test end to end

1. Run `dotnet test runtime/Fyrd.sln`, `cd console && npm test`, `npx playwright test` (in `console/`), and `node --test "scripts/*.test.mjs"`.
2. Run a manual smoke test against a scratch repo: `export GH_TOKEN=…` then `/fyrd:up`. `/fyrd:status` shows `GitHub: signed in as <you>`. Start once without the token to see the warning and the console note. Then:
   - Open a draft PR with a failing check. The row shows a red dot, and the details pane shows counts and the failing-check link.
   - Use Fix failing checks and confirm the Builder receives the message.
   - Use Request validation and confirm the Controller starts a Validator.
   - Mark ready. Merge appears within about 60 s of green with no reselect.
   - Stack three PRs and run Merge stack. All three squash-merge top-down, and `main` has one squashed commit containing all three changes.

## Risks and open questions

- **GraphQL cost.** Rollup `contexts(first:100)` on 50 open PRs per repo per minute costs points against the 5,000/hour GraphQL budget. Log `rateLimit { cost remaining }` (add it to the query) and measure it on the first real repo. If it's too high, drop to `first:30` open PRs, fetch `contexts` only for owned PRs, or raise the TTL. Rate-limit responses back off until the reset time.
- **The token is a long-lived secret** in the owner's shell environment and in the server process. Fyrd only reads it and never persists it. A fine-grained token scoped to the working repos is the safer recommendation.
- **The token is read at startup**, so rotating it needs `/fyrd:down` and `/fyrd:up`. A token that expires mid-run surfaces as `gitHub.error` within 10 minutes, and PR actions return the GitHub error.
- **Without a token, sessions show no PRs at all,** because every PR comes from the listing. This is a regression for owners who relied on a signed-in `gh` without setting `GH_TOKEN`. The `/fyrd:up` warning and the console note make it visible.
- **Stacked PRs are fully owned (D8),** so a session can merge or mark ready a PR on another session's branch. That includes the root of a stack that no Fyrd session opened, when a Builder stacked on someone else's open PR. The guard (4a) and the merge confirm both name the PR and its base. The "stacked" tag and "also on" show that the PR belongs to another session's work.
- **Stacks grow the PR list.** Every session in a five-PR stack lists all five PRs. Rows show `N PRs` with the worst status, so a failing PR in a stack shows red on every session in that stack. That's intended, but it is noisier.
- **Behaviour parity.** The rollup states that `gh` reported (`STALE`, `STARTUP_FAILURE`, `EXPECTED`, …) must map the same way from GraphQL enums. Port the existing `Parse` tests to GraphQL fixtures before deleting `GhCli`.
- **Fyrd Reviewers comment and don't approve,** so `reviewDecision` stays null. "Reviewed" relies on `latestReviews`. Repos that require approvals show `MergeState: blocked`, and the Merge error comes from GitHub.
- **Squash-merging stacks top-down** relies on each child's base being the parent's head branch. If a Builder rebased or retargeted a child onto `main`, the chain detection simply won't link them. That is safe.
- **Repos that disallow squash** fail on Merge with GitHub's "merge method not allowed" error (D1 accepted this trade-off).
- **Stack-merge jobs are in memory** (see Phase 4b). This is acceptable for a single-owner local server.
- **Persona update.** The new Controller routing row only takes effect after the owner accepts the persona update in Settings and the Controller is replaced or re-seeded.
- **Deferred (D3):** a cross-session Pull requests tab and Inbox "needs you" cards. Revisit after Phase 2, once rows carry status. "Update branch" for `behind` PRs and auto-merge are also out of scope.
- **Plugin version.** Each phase's PR bumps `.claude-plugin/plugin.json` once. On conflicts in a stack, bump past both sides.
