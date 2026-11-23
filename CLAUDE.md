# Fyrd

## Plugin versioning

- Bump `version` in `.claude-plugin/plugin.json` once per pull request into the default branch (`main`).
  Not per commit: intermediate commits on a branch keep the version unchanged; the PR carries a single bump.
  Claude Code caches the plugin per version (`~/.claude/plugins/cache/fyrd/fyrd/<version>/`), so an unbumped change keeps running stale code.
- On a merge or rebase conflict over the version, resolve it by bumping again past both sides (max(ours, theirs) + patch). Never take theirs.
- After merging, refresh with `/plugin marketplace update fyrd`, then `claude plugin update fyrd@fyrd` in a terminal (the marketplace update alone leaves the installed plugin at its old version), then `/reload-plugins` (or restart Claude Code), `/fyrd:down` and `/fyrd:up`.
  Then refresh the environment snapshot: cloud sessions and their persona subagents use the fyrd plugin as the environment's setup script installed it when its snapshot was built, so have the environment build a new snapshot (it does when you change its setup script; see `docs/session-skills.md`, Limits).
