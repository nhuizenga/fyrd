# Controller account skills

Four claude.ai account skills the Fyrd Controller cloud session uses: `fyrd-research`, `fyrd-build`, `fyrd-peasant`, `fyrd-watch`. Each description starts with `Fyrd Controller only:` so it doesn't trigger in other sessions. The rules live in `personas/hub/controller.md`; each skill gives the tool-call steps for one request type.

## Upload (manual step)

Upload all four skills together. `fyrd-research`, `fyrd-build` and `fyrd-watch` point to `fyrd-peasant` for the session-cap and teardown steps, so none of them is complete on its own.

For each folder here (`fyrd-research`, `fyrd-build`, `fyrd-peasant`, `fyrd-watch`):

1. Zip the folder so `SKILL.md` is inside it (for example `fyrd-build/SKILL.md`).
2. On claude.ai open Settings > Capabilities > Skills, choose Upload skill, and select the zip.
3. Make sure the skill is enabled.

Re-upload a skill whenever its `SKILL.md` changes.

## Create the Controller (manual step)

Fyrd doesn't create the Controller. Sessions Fyrd creates through the API get no claude.ai connectors, so a Fyrd-made Controller would have no `claude-code-remote` tools and couldn't start peasants. A session you create by hand on claude.ai has them.

1. Open https://claude.ai/code and start a new session. It needs no repo.
2. Copy its session id (the `session_…` part of its URL).
3. In the Fyrd console open Sessions, paste the id into the **Adopt Controller** panel and choose Adopt.

Fyrd sends the Controller its instructions, settings and memory as the first message.

## After edits

Skills load when a session starts. After uploading changed skills or editing a persona, open Sessions in the Fyrd console and choose **Replace Controller** next to Open Controller. That archives the current Controller. Then create a fresh session on claude.ai and adopt it as above, so the new Controller picks up the changes.
