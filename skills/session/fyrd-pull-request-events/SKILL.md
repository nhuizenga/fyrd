---
name: fyrd-pull-request-events
description: Pull request events: handling a review, review comment, failing check, merge or close that reaches you as an event, your own review included. Use before you post your review in step 7, and when a pull request event reaches you.
roles: builder
---
# Pull request events

- The review you post yourself in step 7 comes back as a review-comment event authored by you: it is not new work, so don't start a worker for it. Act only on comments and reviews from others.
- Review comments and failing checks that reach you after review are work: start a fresh worker to fix them, run the tests and push, validate again, then update the PR body and reply on each thread as Pull request hygiene says. A fix made after your report is validated but not re-reviewed unless the owner asks; say in your reply that the new head is unreviewed. Never resolve a reviewer's thread for them.
- When a PR is merged or closed, call `mcp__claude-code-remote__unsubscribe_pr_activity` for it. When every PR is merged or closed, stop.
