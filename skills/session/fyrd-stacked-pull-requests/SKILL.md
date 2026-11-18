---
name: fyrd-stacked-pull-requests
description: "Stacked pull requests: building, opening, fixing, validating and moving work in a native GitHub stack. Use when your work is, or may be, split into more than one pull request."
roles: builder
---
# Stacked pull requests

Several PRs that build on each other are one native GitHub stack, not just branches based on each other. Make and keep it with GitHub's APIs only (the GitHub tools you have, or the REST and GraphQL endpoints below with the `X-GitHub-Api-Version: 2026-03-10` header); never shell out to the `gh` CLI.

- **Branches.** Each branch starts from the one below it; the bottom one starts from your base branch. Workers push them with plain `git push`. For a stack, start one worker per branch, bottom first, each after the one below it returns; never run two workers on the same branch.
- **Briefs.** For a worker building an upper branch of a stack, add the body the worker below returned, as a start for finding the rules; its own body lists only the rules and guards its own diff touches.
- **Open.** When every branch is pushed, open one draft PR per branch, bottom first: the bottom PR's base is your base branch, and each PR above is based on the branch below it. Then create the stack with `POST /repos/{owner}/{repo}/stacks` and `{"pull_requests": [<bottom>, …, <top>]}` (201 returns the stack and its `number`). To add PRs to the top later, `POST /repos/{owner}/{repo}/stacks/{stack_number}/add` with only the new PR numbers, bottom first. Subscribe to each PR as in step 4.
- **Fixes lower in the stack.** After a worker pushes a fix to a lower branch, bring every branch above it up to date, bottom up, by merging the branch below into it: `POST /repos/{owner}/{repo}/merges` with `base` set to the upper branch and `head` to the branch below (201 merged, 204 already up to date). On a 409 merge conflict, start a worker to merge the branch below into the upper one, resolve the conflict, run the tests and push normally. The pull request "update branch" endpoint refuses stacked PRs, so don't use it.
- **Validation.** In a stack, finish the round's fixes and merges first, then validate each changed PR once, bottom up, not after every push.
- **Moved work.** Work moves from one PR of a stack to another with new commits and is called out in both PR bodies. Both PRs are validated again, and re-reviewed on what changed as step 7 says; the move counts as one of step 7's rounds, and after the report it follows Pull request events.
- **Check it.** `GET /repos/{owner}/{repo}/stacks?pull_request=<n>` shows the stack a PR belongs to and each PR's state.
- **Not available.** If creating the stack fails with any non-2xx response (404 or 422 when stacked PRs aren't enabled or the PRs don't chain; 403, 5xx or a network error otherwise), or your tools can't reach these endpoints, keep the chained draft PRs and ask the owner with `AskUserQuestion`: `Enable stacked PRs and retry`, `Keep chained PRs without a stack` or `Squash into one PR`.
- Never merge a stack or any PR in it, and never unstack it unless the owner asks.
