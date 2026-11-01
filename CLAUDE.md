# Fyrd

## Plugin versioning

- Bump `version` in `.claude-plugin/plugin.json` with every new piece of work (feature, fix, branch).
  Claude Code caches the plugin per version (`~/.claude/plugins/cache/fyrd/fyrd/<version>/`), so an unbumped change keeps running stale code.
- On a merge or rebase conflict over the version, resolve it by bumping again past both sides (max(ours, theirs) + patch). Never take theirs.
- After merging, refresh with `/plugin marketplace update fyrd`, then `/fyrd:down` and `/fyrd:up`.
