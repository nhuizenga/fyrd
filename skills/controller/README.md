# Controller account skills

Four claude.ai account skills the Fyrd Controller cloud session uses: `fyrd-research`, `fyrd-build`, `fyrd-peasant`, `fyrd-watch`. Each description starts with `Fyrd Controller only:` so it doesn't trigger in other sessions. The rules live in `personas/hub/controller.md`; each skill gives the tool-call steps for one request type.

## Upload (manual step)

Upload all four skills together. `fyrd-research`, `fyrd-build` and `fyrd-watch` point to `fyrd-peasant` for the session-cap and teardown steps, so none of them is complete on its own.

For each folder here (`fyrd-research`, `fyrd-build`, `fyrd-peasant`, `fyrd-watch`):

1. Zip the folder so `SKILL.md` is inside it (for example `fyrd-build/SKILL.md`).
2. On claude.ai open Settings > Capabilities > Skills, choose Upload skill, and select the zip.
3. Make sure the skill is enabled.

Re-upload a skill whenever its `SKILL.md` changes.

## After edits

Skills load when a session starts, and account skills load in API-created sessions such as the Controller (hub spike, run 6). After uploading changed skills, open Sessions in the Fyrd console and choose **Recreate Controller** next to Open Controller, so a new Controller picks them up.
