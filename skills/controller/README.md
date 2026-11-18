# Controller skills

Five skills the Fyrd Controller cloud session uses: `fyrd-research`, `fyrd-build`, `fyrd-investigate`, `fyrd-peasant`, `fyrd-watch`. Each description starts with `Fyrd Controller only:`. The rules live in `personas/hub/controller.md`; each skill gives the tool-call steps for one request type.

## Delivery (automatic)

Nothing is uploaded to claude.ai. The Fyrd service reads `skills/controller/*/SKILL.md` and embeds each skill in the Controller's message as a `<controller-skill>` block, after the peasant personas and the subagent persona texts. It reads the files on every poll pass, so a `git pull` is enough.

The same message carries one `<peasant-persona>` block per role with the whole persona, which the Controller copies into the `append_system_prompt` of a session of that role, and `<subagent-persona-text>` blocks (`validator`, `reviewer` once `for="builder"` and once `for="researcher"`, `investigator`). Those hold only the part of a persona that a subagent needs; the service cuts them from the stored persona by heading (`runtime/src/Fyrd.Core/Hub/SubagentPersonas.cs` has the keep-lists) each time it builds the message, and falls back to the whole persona when a part it needs is gone: a heading or the Inputs bullet renamed, the investigator's Untrusted content bullet renamed or removed (the validator and reviewer personas have none, so for them it is kept only when present), or the reviewer's "Whoever does the reviewing…" paragraph moved out of Reviewing a plan or reworded at its start. A persona with an empty definition gets no block. The skills tell the Controller to copy them, not whole personas, into the `<subagent-persona>` blocks of a Builder, Researcher, Validator or Monitor.

The first time a service that has this feature polls an existing Controller, it sends the instructions once (there is no stored hash yet); that is how existing Controllers receive the inline skills. After that, when a skill, a persona, or the Controller settings change, the service re-sends the instructions to the live Controller as soon as it is idle. Persona edits made in Settings reach the Controller the same way. The message says the latest instructions replace earlier ones.

## One-time cleanup

If you uploaded the `fyrd-*` skills to claude.ai earlier, remove the uploaded fyrd-* skills: open Settings > Capabilities > Skills and delete (or disable) `fyrd-research`, `fyrd-build`, `fyrd-peasant` and `fyrd-watch`. Until you do, a stale copy stays visible to every session. The Controller prefers the inline blocks when names clash.

## Create the Controller

In the Fyrd console open Sessions and choose **Create Controller**. Fyrd runs your signed-in `claude` CLI as `claude --cloud controller` to start a new cloud session at https://claude.ai/code, reads the new session's id from what the CLI prints, then sends the whole opening prompt (instructions, skills, settings and memory) to it with `claude -p --cloud <id>`, so the session is your own and reads the prompt as yours. The CLI only creates a session from an interactive terminal, so Fyrd runs it in a hidden pseudo terminal (ConPTY on Windows). To check that part on your Windows machine without creating a session, run `pwsh ./scripts/test-conpty.ps1`. It runs from `<FYRD_HOME>/hub/controller-launch`, which isn't a git repository, so the session needs no repo and starts empty. It runs in your CLI's default cloud environment: the hub's `environment_id` setting is not passed to the CLI (`--environment` only takes self-hosted `ccpool_` environments).

- **The session's first message is just "controller":** that is the description the CLI starts it with. The opening prompt that follows says so.
- **First-run prompts:** if the CLI asks whether to trust `controller-launch`, or asks first-run setup questions, Fyrd stops it and says so. Open a terminal in that folder, run `claude`, answer the prompt once, type `/exit`, and create the Controller again.
- **If the prompt isn't delivered:** the session was still created and Fyrd keeps it. Choose **Create Controller** again: Fyrd resends the prompt to that same session instead of creating another one.

## After edits

Skill and persona edits reach a live Controller by themselves. Choose **Replace Controller** next to Open Controller only if you want a fresh conversation: that archives the current Controller. Then choose **Create Controller** as above.
