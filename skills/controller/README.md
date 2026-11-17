# Controller skills

Five skills the Fyrd Controller cloud session uses: `fyrd-research`, `fyrd-build`, `fyrd-investigate`, `fyrd-peasant`, `fyrd-watch`. Each description starts with `Fyrd Controller only:`. The rules live in `personas/hub/controller.md`; each skill gives the tool-call steps for one request type.

## Delivery (automatic)

Nothing is uploaded to claude.ai. The Fyrd service reads `skills/controller/*/SKILL.md` and embeds each skill in the Controller's message as a `<controller-skill>` block, after the peasant personas. It reads the files on every poll pass, so a `git pull` is enough.

The first time a service that has this feature polls an existing Controller, it sends the instructions once (there is no stored hash yet); that is how existing Controllers receive the inline skills. After that, when a skill, a persona, or the Controller settings change, the service re-sends the instructions to the live Controller as soon as it is idle. Persona edits made in Settings reach the Controller the same way. The message says the latest instructions replace earlier ones.

## One-time cleanup

If you uploaded the `fyrd-*` skills to claude.ai earlier, remove the uploaded fyrd-* skills: open Settings > Capabilities > Skills and delete (or disable) `fyrd-research`, `fyrd-build`, `fyrd-peasant` and `fyrd-watch`. Until you do, a stale copy stays visible to every session. The Controller prefers the inline blocks when names clash.

## Create the Controller

In the Fyrd console open Sessions and choose **Create Controller**. Fyrd runs your signed-in `claude` CLI (`claude -p --cloud -n "Fyrd Controller"`) to start a new cloud session at https://claude.ai/code and sends the whole opening prompt (instructions, skills, settings and memory) as its first turn, so the session is your own and reads that turn as yours. It needs no repo. It runs in your CLI's default cloud environment: the hub's `environment_id` setting is not passed to the CLI (`--environment` only takes self-hosted `ccpool_` environments).

## After edits

Skill and persona edits reach a live Controller by themselves. Choose **Replace Controller** next to Open Controller only if you want a fresh conversation: that archives the current Controller. Then choose **Create Controller** as above.
