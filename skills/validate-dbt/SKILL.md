---
name: validate-dbt
description: "Validate dbt: the checks a dbt change needs before merge that its PR checks may skip, and a report per test class. Use when a diff touches a dbt project's models/, macros/, tests/, seeds/ or dbt_project.yml, and before you start or request a warehouse build."
roles: builder
---
# Validate dbt

Checks for a change to a dbt project: for the worker that made it before it returns, for the Validator in its steps 2 and 3, and for the Builder before it starts or requests a warehouse build. dbt is the same in every repo; how a repo runs it is not. So first find the repo's own way, and prefer it to the generic commands below: its PR and deploy workflows (`.github/workflows`), its agent commands and skills (`.claude/commands`, `.claude/skills`), its contributor and agent docs, and the targets in its dbt profiles.

## Limits

- Build and write only into an isolated dev, sandbox or preview schema or target that the task or the repo's docs give you (a schema of your own, a PR preview). Never prod, never a shared schema, and never a target you only found credentials for.
- On live tables, in any env the model deploys to, run read-only queries only, and only where the repo's docs allow it.
- Where no such target exists or the docs don't allow the query, report the check as not run and why, never as a pass.
- A run counts only for the head it ran on: run it again after the head moves, or name it as a run on an older head.

## The checks

1. **Map PR checks against deploy.** From the workflows, list which of parse, compile, `run --empty`, unit tests, data tests, singular tests and builds on real rows run at PR time and which only after merge. Read each PR workflow's state (`GET /repos/{owner}/{repo}/actions/workflows/{file}`): a disabled one runs nothing. What runs only after merge is what checks 2 to 7 must cover.
2. **Compile what reads the change.** `dbt compile --select <changed>+` against your dev target. Parse alone misses singular tests and macros that read a model's config.
3. **Build and test downstream on real rows.** `dbt build --select <changed>+` (or `--select state:modified+ --state <base manifest>`) into an isolated schema or preview, with its data tests. Run it again after the head moves.
4. **Selector hygiene for narrow builds.** With eager indirect selection, a test is selected when any of its parents is, so a test whose other parent is outside the selection fails or errors on a table the run never built. List the selected tests (`dbt ls --select <selection> --resource-type test`) and their parents. Then exclude those tests (for example `--exclude test_name:relationships`), add their parents to the selection, or use `--indirect-selection cautious` or `--defer --state <manifest>`. Prove each excluded test in another run and say which.
5. **Incremental and custom materializations.** Build with a full refresh, then build incrementally into a fresh schema or suffix. Seed rows whose key or date crosses every boundary: into the window from below, beyond any lookback, and out of the window. Assert the grain on the result: `group by <key> having count(*) > 1` returns no rows.
6. **Grain or unique-key changes.** Run a read-only duplicate query on the live table in each env the model deploys to, where the repo's docs allow it.
7. **Cross-env schema.** Each source column the diff newly references exists in every env the model runs in, or the model guards against its absence. A failed `--empty` or compile leg that names a column the diff adds is the PR's own failure, not a pre-existing one.
8. **Pre-existing claims.** A failure is pre-existing only when each error of each matrix leg matches the same error on the base branch, in a run you link. List every error per leg, not only the first. A step skipped after an earlier failure has not run; it has not passed.
9. **Repo checklists.** Work through the repo's own model-review checklist, and run its read-only breaking-change commands (in `.claude/commands`, `.claude/skills` or its docs). Where the repo's docs reserve a command for people, don't run it: name it as not run. A bot or reviewer check that errored or was truncated is undone, not passed.
10. **Test-time drift.** For a consistency test that compares a model with live sources, record the time between the build and the test, and say which warnings that gap could explain.
11. **Report per test class.** For unit, generic, relationships, singular and heavy tests (or the classes the repo's own tags name): pass, warn or not run, each with its run link or command and the head it ran on, and why for each not run. This report goes into the PR body.

## Who runs what

- **A worker** that changed the dbt project runs the checks that apply before it returns, and puts the report in its result.
- **The Validator** runs them at the PR's current head in steps 2 and 3, where step 3's limits allow a target; otherwise it names a run of the repo's own preview build at that head (link and commit), or reports them as not run.
- **The Builder**, before it starts a worker that builds in a warehouse or requests a build itself (a bot command, a workflow dispatch), checks against check 1 and the limits above that the target is isolated, the selection covers `<changed>+` with check 4's hygiene, and the build runs at the PR's current head.
