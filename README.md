# fyrd

A Claude Code plugin.

## Layout

```
.claude-plugin/
  plugin.json        # plugin manifest
  marketplace.json   # lets this repo act as its own marketplace
skills/<name>/SKILL.md
commands/<name>.md   # slash commands -> /fyrd:<name>
agents/<name>.md     # subagents
hooks/hooks.json     # hook config (use ${CLAUDE_PLUGIN_ROOT} for paths)
```

## Local install

```
/plugin marketplace add C:/Users/nhuizenga/repos/personal/fyrd
/plugin install fyrd@fyrd
```

After edits, run `/reload-plugins`.
