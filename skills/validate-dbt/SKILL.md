---
name: validate-dbt
description: "Validate dbt: the checks a dbt change needs before merge that its PR checks may skip, and a report per test class. Use when a diff touches a dbt project's models/, macros/, tests/, seeds/, snapshots/, dbt_project.yml, packages.yml or selectors.yml, wherever the project sits, and before you start or request a warehouse build."
roles: builder, validator
---
# Validate dbt

Checks for a change to a dbt project (see Who runs what). First find how the repo runs dbt, and prefer that to the generic commands below: its PR and deploy workflows (`.github/workflows`), agent commands and skills (`.claude/commands`, `.claude/skills`), contributor and agent docs, and the targets in its dbt profiles.

## Limits

- Build and write only into an isolated dev, sandbox or preview schema or target that the task or the repo's docs give you (a schema of your own, a PR preview). Never prod, a shared schema, or a target you only found credentials for.
- Always pass `--target <isolated target>`, and check where the build writes, not the target's name. Before any build, list the relations it resolves to (`dbt ls --select <selection> --resource-type model seed snapshot test --output json --output-keys "unique_id relation_name"`, or `relation_name` in the compiled manifest) and confirm each is in the isolated schema or database. A test's relation is where `store_failures` writes, an audit schema its `schema` config can send outside the target. Read the other writes too: pre-hooks, post-hooks, grants, and the `on-run-start` and `on-run-end` hooks of the project and of every installed package. If any of them lands outside the isolated target, report the check as not run.
- On live tables, in any env the model deploys to, run read-only queries only, and only where the repo's docs allow it.
- Where no such target exists or the docs don't allow the query, report the check as not run and why, never as a pass.
- A run counts only for the head it ran on: run it again after the head moves, or name it as a run on an older head.

## The checks

1. **Map PR checks against deploy.** From the workflows, list which of parse, compile, `run --empty`, unit tests, data tests, singular tests and builds on real rows run at PR time and which only after merge. Read each PR workflow's state (`GET /repos/{owner}/{repo}/actions/workflows/{file}`): a disabled one runs nothing. What runs only after merge is what checks 2 to 7 must cover.
2. **Compile what reads the change.** `dbt compile --select <changed>+` against your dev target. Parse alone misses singular tests and macros that read a model's config.
3. **Build and test downstream on real rows.** `dbt build --select <changed>+` (or `--select state:modified+ --state <dir>`) into an isolated schema or preview, with its data tests, not only `dbt parse` and unit tests. `<dir>` holds the base branch's `manifest.json`: run `dbt parse --target-path <dir>` on a checkout of the base.
4. **Selector hygiene for narrow builds.** With eager indirect selection, a test is selected when any of its parents is, so a test whose other parent is outside the selection errors on a table the run never built. Use `--indirect-selection cautious` (or `buildable`), or add those parents to the selection. Cautious and buildable drop such tests silently, so they did not run: list them (eager `dbt ls --select <selection> --resource-type test` minus the same with cautious), and prove each in another run that builds its parents or report it as not run in check 11. `--defer` points unselected parents at the deferred environment's tables, usually prod, so it is allowed only under the live-read limit; it needs that environment's deploy manifest as `--state`, and `--favor-state` widens what it reads.
5. **Incremental and custom materializations.** Build with `--full-refresh` into a fresh isolated schema, then incrementally on that schema. Add rows whose key or date crosses every boundary (into the window from below, beyond any lookback, out of it) only to copies of the sources in the isolated target, never to a source table. A `source()` reads its configured schema whatever the `--target`, so point the sources at the copies (a source `schema:` or `database:` from a var or env var, or the repo's own override) and confirm in the compiled SQL that the model reads them. Assert the grain on the result: `group by <key> having count(*) > 1` returns no rows. Unit tests with `overrides: macros: {is_incremental: true}` and a `this` fixture prove only the incremental filter, not the merge, delete+insert or append, so not the grain; they need the model and its parents built first (`dbt run --select +<model> --empty` in the isolated target). Without the two-run build on boundary rows, report the merge and grain as not run.
6. **Grain or unique-key changes.** Run a read-only duplicate query on the live table in each env the model deploys to, where the repo's docs allow it.
7. **Cross-env schema.** Each source column the diff newly references exists in every env the model runs in, or the model guards against its absence. A failed `--empty` or compile leg naming a column the diff adds is the PR's own failure, not a pre-existing one.
8. **Pre-existing claims.** A failure is pre-existing only when each error of each matrix leg matches the same error on the base branch, in a run you link. List every error per leg, not only the first. A step skipped after an earlier failure has not run, not passed.
9. **Repo checklists.** Work through the repo's own model-review checklist, and run its read-only breaking-change commands (in `.claude/commands`, `.claude/skills` or its docs). Where its docs reserve a command for people, don't run it: name it as not run. A bot or reviewer check that errored or was truncated is undone, not passed.
10. **Test-time drift.** For a consistency test comparing a model with live sources, record the time between the build and the test, and say which warnings that gap could explain.
11. **Report per test class.** For unit, generic, relationships and singular tests, and the classes the repo's own tags name (for example a `heavy` tag): pass, warn or not run, each with its run link or command and the head it ran on, and why for each not run. This report goes into the PR body.

## Who runs what

- **A worker** that changed the dbt project runs the checks that apply before it returns, and puts the report in its result.
- **The Validator** runs them at the PR's current head in its steps 2 and 3, where its step 5 limits allow a target, else links a run at that head that did, or reports them as not run.
- **The Builder**, before it starts a worker that builds in a warehouse or requests a build itself (a bot command, a workflow dispatch), checks against check 1 and the limits above that the target is isolated, the selection covers `<changed>+` with check 4's hygiene, and the build runs at the PR's current head.
