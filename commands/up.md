---
description: Start the Fyrd console (backend + UI) and open it in the browser
allowed-tools: Bash(node:*)
---

Run `node "${CLAUDE_PLUGIN_ROOT}/scripts/fyrd.mjs" up` and relay its output to the user in one line.

If the output says `GH_TOKEN is not set`, include that line too: PR status and PR actions stay off until the owner sets `GH_TOKEN` (or `GITHUB_TOKEN`) and runs `/fyrd:down` then `/fyrd:up`.
