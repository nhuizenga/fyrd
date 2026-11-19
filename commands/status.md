---
description: Show whether Fyrd is running and what is waiting on you
allowed-tools: Bash(node:*)
---

Run `node "${CLAUDE_PLUGIN_ROOT}/scripts/fyrd.mjs" status` and relay its output to the user in one line.

If the output says `Session skills: out of date`, include that line too: sessions get the skills' text inline until the owner pastes the new setup script (Settings, Session skills, in the console) and confirms it there.
