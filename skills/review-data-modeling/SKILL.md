---
name: review-data-modeling
description: "Review data modeling: the checks a second Reviewer runs on a dbt or warehouse diff, and the questions only someone with warehouse access can settle. Use when you start the step 7 review of a PR whose diff touches a dbt project (models/, macros/, tests/, seeds/, snapshots/, dbt_project.yml, packages.yml, selectors.yml, wherever it sits) or other warehouse SQL or schedules."
roles: builder
---
# Review data modeling

Checks for the second Reviewer that the Builder starts in step 7 on a PR whose diff touches a dbt project (`models/`, `macros/`, `tests/`, `seeds/`, `snapshots/`, `dbt_project.yml`, `packages.yml`, `selectors.yml`, wherever it sits) or other warehouse SQL or schedules, beside the general review. The general review reads the code; this one asks what the change does to the data: which rows it keeps, how many there are, and what it costs to build them. Most of that the diff doesn't show, so a check you can't settle from the code becomes a question, not a guess. First find the repo's own model-review checklist and data docs (its contributor and agent docs, `.claude/commands`, `.claude/skills`) and apply them too.

## Limits

- You read code: the diff, the models and tests around it, their configs and docs, and what the PR's CI logs show.
- Run a read-only query on the warehouse only where your session has a read-only warehouse connector and the repo's docs allow such queries. Otherwise run nothing, and name the check under questions (check 7). Prefer metadata, `information_schema`, or a partition- or date-bounded query; name a full scan as a question instead.
- Report only aggregates from a query (counts, bytes, durations) and the names of grouped columns, never their values, row values or sample rows.
- Never write: no builds, no DDL or DML, nothing into any schema, table or stage, in any env. Never use a credential you merely found in the environment.
- An estimate you didn't measure says so, and says what it is based on.

## The checks

1. **Grain and uniqueness.** State each new or changed model's grain (what one row is) and confirm a uniqueness test on that key. Question every dedup step (`row_number`, `qualify`, `distinct`) against the source's real cardinality: a dedup over a source already unique on its key costs a scan and does nothing, and a dedup on a key that isn't the grain drops real rows.
2. **Rules that drop rows.** For each filter, exclusivity rule, inner join or dedup that removes rows and that the diff adds or changes, or whose input the change alters, estimate how many it removes and which. A rule justified by a collision or edge case nobody has measured is a finding: ask for the count.
3. **Relationships liveness.** Where a child model that the diff adds or changes, or whose input the change alters, references a parent (a relationships test, a join to a dim), the child's liveness filter (active, not deleted, current version, date window) matches the parent's; otherwise valid rows fail the test, or orphans pass. A relationships or membership test that only warns is a finding to explain, not a pass.
4. **Cost and schedule.** For each model that the diff adds or changes, or whose input the change alters, check the tag or selector that sets its cadence, the warehouse or compute size it runs on (for hourly and other frequent models above all), whether the incremental filter prunes on the source (no function wrapped around the pruning column), and the merge volume per run. A frequent model that scans or merges its whole table each run is a finding.
5. **Deploy-only checks.** Apply the Reviewer's Deploy-only checks to the models and to what uses them (downstream models, exposures, dashboards, exports).
6. **Removed names.** When the change removes or renames something other content binds to by name (a model, column, alias, field, endpoint, saved query), list its consumers in every env the change deploys to, not only in this repo: content deleted from the repo but still live (search the repo history for deleted files that use the name), content users made in the live system, and the base-branch version of any consumer this PR also edits if the deploy applies the removal first. A removal that still has consumers needs an expand-then-contract split: keep the old name, hidden or aliased, until every consumer has moved. A PR whose only test of this is the deploy itself is a **Should fix** unless its body names the risk and a merge gate.
7. **Questions for someone with warehouse access.** For each check you couldn't settle from the code or an allowed read-only query, write the question only someone with warehouse access can answer, with the query that would settle it (a duplicate count on the key, the rows a rule removes, bytes scanned per run). These are questions, not runs.

## Report

Return the findings in the format Reviewing code gives (by severity, each with its file and line, what is wrong, why it matters and a fix), then the questions of check 7. For each check, say how it was settled: from the code, by a read-only query (which, and when), left as a question, or not applicable to this diff.
