# Controller account skills

Four claude.ai account skills the Fyrd Controller cloud session uses: `fyrd-research`, `fyrd-build`, `fyrd-peasant`, `fyrd-watch`. Each description starts with `Fyrd Controller only:` so it doesn't trigger in other sessions. The rules live in `personas/hub/controller.md`; each skill gives the tool-call steps for one request type.

## Upload (manual step)

For each folder here (`fyrd-research`, `fyrd-build`, `fyrd-peasant`, `fyrd-watch`):

1. Zip the folder so `SKILL.md` is inside it (for example `fyrd-build/SKILL.md`).
2. On claude.ai open Settings > Capabilities > Skills, choose Upload skill, and select the zip.
3. Make sure the skill is enabled.

Re-upload a skill whenever its `SKILL.md` changes.

## After edits

Skills load when a session starts. After uploading changed skills, Recreate the Controller from the Fyrd console so the new session picks them up.

## Fallback if skills don't load

The hub spike showed account skills appear in claude.ai/code sessions. Whether they load in API-created sessions (how the Controller is created) is still unverified. If the Controller can't see the skills after Recreate, the fallback is to append the four skill bodies to `HubPrompt.Controller`. That follow-up is filed, not built here.
