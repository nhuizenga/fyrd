# Controller skills

Five skills the Fyrd Controller cloud session uses: `fyrd-research`, `fyrd-build`, `fyrd-investigate`, `fyrd-peasant`, `fyrd-watch`. Each description starts with `Fyrd Controller only:`. The rules live in `personas/hub/controller.md`; each skill gives the tool-call steps for one request type.

## Delivery (automatic)

Nothing is uploaded to claude.ai. The Fyrd service reads `skills/controller/*/SKILL.md` and embeds each skill in the Controller's message as a `<controller-skill>` block, after the peasant personas. It reads the files on every poll pass, so a `git pull` is enough.

The first time a service that has this feature polls an already-adopted Controller, it sends the instructions once (there is no stored hash yet); that is how existing Controllers receive the inline skills. After that, when a skill, a persona, or the Controller settings change, the service re-sends the instructions to the live Controller as soon as it is idle. Persona edits made in Settings reach the Controller the same way. The message says the latest instructions replace earlier ones.

## One-time cleanup

If you uploaded the `fyrd-*` skills to claude.ai earlier, remove the uploaded fyrd-* skills: open Settings > Capabilities > Skills and delete (or disable) `fyrd-research`, `fyrd-build`, `fyrd-peasant` and `fyrd-watch`. Until you do, a stale copy stays visible to every session. The Controller prefers the inline blocks when names clash.

## Create the Controller (manual step)

Fyrd doesn't create the Controller. Sessions Fyrd creates through the API get no claude.ai connectors, so a Fyrd-made Controller would have no `claude-code-remote` tools and couldn't start peasants. A session you create by hand on claude.ai has them.

1. Open https://claude.ai/code and start a new session. It needs no repo.
2. Copy its session id (the `session_…` part of its URL).
3. In the Fyrd console open Sessions, paste the id into the **Adopt Controller** panel and choose Adopt.

Fyrd sends the Controller its instructions, skills, settings and memory as the first message.

## After edits

Skill and persona edits reach a live Controller by themselves. Choose **Replace Controller** next to Open Controller only if you want a fresh conversation: that archives the current Controller. Then create a fresh session on claude.ai and adopt it as above.
