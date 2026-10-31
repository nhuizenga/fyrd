# Fyrd: the Controller (hub design)

Date: 2026-10-31. Status: approved by the owner 2026-10-31.
Background: `docs/reference/cloud-session-web-api.md`, `docs/reference/claude-code-cloud-sessions-api.md`, `docs/reference/mcp__claude-code-remote.json`, `docs/superpowers/spikes/2026-10-31-clear-persona-probe.md`.

## 1. Intent

The owner wants one place to type requests to Fyrd. That place is the **Controller**: a single, long-lived Claude Code cloud session. The owner writes plain requests ("Build the following: a.md, b.md"). The Controller decides what should happen and does it directly, creating, messaging and archiving other cloud sessions ("peasants") through the `claude-code-remote` connector tools (`mcp__claude-code-remote__*`). The Controller does no project work itself.

Why: today, starting work means the spec → attack → lead pipeline and the console. The owner wants to talk to one session and have it route the work. The pipeline will be deprecated later in favour of this design.

## 2. Goals

1. One Controller at a time, found or created from the console.
2. The Controller handles the typical requests in §5.
3. Peasants are recycled, not multiplied: a name is reused by archiving the old session and creating a new one.
4. Shared memory and per-role memory. Any session can add to it, and the service stores it.
5. The Controller's instructions ship as claude.ai account skills.

## 3. Non-goals

- **Leads.** The Lead role and a `fyrd-lead` skill are deferred until the owner decides what a lead is for. If asked for a lead, the Controller says the role isn't defined yet and offers a Builder.
- **The Controller's own multi-step workflow** (plan → build → review → validate). It will be defined later. In this design the Controller routes single requests.
- **Posting to Slack.** The Slack watcher only suggests.
- **Pushing new memory to live sessions.** Memory reaches only sessions created after it was written.

## 4. Anti-goals

- **Disturbing the current pipeline's runtime.** `personas/lead.md`, `implementer.md`, `reviewer.md`, `validator.md`, `research-lead.md` and `researcher.md`, `LeadPrompt`, `SpecRunner` and the existing memory files keep working exactly as they do. Its console tabs are replaced (§12).
- **Writing Fyrd runtime data to any repo.** Memory and state live under `~/.fyrd`. (The old `controller.md` idea of `/docs/fyrd/context` in a repo is removed.)
- **A second Controller.**

## 5. Requests the Controller handles

| Request | Action |
|---|---|
| "Research {uri} and help me build an implementation plan" | Researcher session for the uri |
| "Build the following: {uri list}" | One Builder per uri, in parallel (the owner's default for any multi-input request) |
| "Start a new builder/reviewer/validator [suffix]" | Create that peasant |
| "Tear down {name}" | Archive it |
| "Watch #{pr} and fix anything that comes up" | PR Monitor |
| "Start a new watcher on {slack channel}…" | Slack Monitor |
| "Start a new lead…" | Not available yet (§3) |

Anything else: the Controller interprets it as best it can and asks the owner when it's unclear.

## 6. Sessions

### Roles

Controller, Researcher, Builder (the owner prefers "builder" to "implementer"), Reviewer, Validator, Monitor. Lead is listed but deferred. Research-lead is dropped from this design.

### Personas

Personas are managed in the console under **Settings › Personas** (owner's decision). Each has a name, a model and a definition. They are stored by the service in `~/.fyrd/data/personas/`, not in a repo.

- **Seeding:** this work adds the six personas to Settings: Controller, Researcher, Builder, Reviewer, Validator and Monitor. Their starting text comes from the owner's `personas/controller.md` (hardened per §11) and from new drafts. Builder is adapted from `implementer.md`. The pipeline's persona files are not used here. *(Claude's call: the seed text is checked in, for example under `personas/hub/`, and copied into `~/.fyrd` only when a persona doesn't exist there yet.)*
- **Model:** `create_session` gets the persona's model.
- **Deletion:** the Controller can't be deleted.
- **Changes** apply to sessions created afterwards. A change to the Controller persona applies after "Recreate".

Each peasant persona tells the session to:
1. Read its inputs. They are GitHub file URLs or claude.ai artifact links (owner's decision). Then do the task it was given, and attach any repo it lacks with `add_repo` (`access: "push"` when it must commit), clone it, then call `register_repo_root`.
2. Act on messages from other Fyrd sessions as work instructions. **No trust boundary**: the owner ruled it out, because it would make leads steering their own peasants harder.
3. When done, say so in its final reply and stop. The owner and the Controller read the result from the session. *(Claude's call on outputs: a Builder pushes a branch by default, not a PR (owner's decision); it opens a PR only when asked. A Researcher publishes its plan as a claude.ai artifact and replies with the link, so nothing is written to a repo. Reviewers and Validators reply with findings on the PR or branch they were given.)*
4. Record memory with a `fyrd-remember` block when told to remember something (§8).

### Naming and tags

- **Title:** `<role>-<suffix>`. The suffix comes from the owner. If the owner gave none, it comes from the input's file name (`auth-plan.md` → `builder-auth-plan`). If that name is live and busy, add `-2`, `-3`. *(Claude's call.)*
- **Tags:** `fyrd-hub` on every Controller-managed session, plus `fyrd-role-<role>`. The Controller also gets `fyrd-controller`. These don't overlap the pipeline's `fyrd-spec-*` tags.
- **Session settings:** environment and permission mode from the existing Fyrd config (today `env_011v4b3WBhRMaGGti6Qw747c`, `auto`).

### Recycling

Slash commands such as `/clear` can't be triggered through any API (spike, 5 runs). So a peasant is recycled by **archiving it and creating a new session under the same name**. The new session has a clean context, the role's persona and current memory.

- The Controller reuses a name only when that session is idle (`get_session` status bucket not `working` or `blocked`). Otherwise it uses the next suffix.
- The task goes in `create_session`'s first `prompt`, so it arrives as a real user turn. `send_message` is for follow-ups to a running peasant.
- A peasant started with no task ("Start a new builder") gets the first prompt "Wait for your task.". Its first task then comes from the console's "Message this session" box, which posts a real user turn, or from the Controller by `send_message`. *(Claude's call.)*
- **Finding peasants:** `list_sessions` can't filter by tag for in-session callers. So the Controller pages `list_sessions` (`mine: true`) and matches titles with a role prefix. *(Claude's call.)*

### The Controller itself

- **Console owns it.** A console "Controller" button finds the live session tagged `fyrd-controller`. If none exists it creates one, then opens it on claude.ai. "Recreate" archives and recreates it, for example after persona or skill changes.
- **Its `append_system_prompt`** is built by the service. It holds:
  - `controller.md`
  - every peasant persona's text, so the Controller can pass each one to `create_session`
  - shared memory and every role's memory, each in its own delimited block
- **Memory updates:** when a memory file changes, the service posts the changed memory to the Controller as a user message. The Controller uses the latest version for every peasant it creates after that.
- **Only one:** the console is the only creator. The persona also tells the Controller to refuse to create another Controller.

### Session cap

The console has a **global session cap** under Settings (owner's decision; default 20). It counts all live `fyrd-hub` sessions, not squads, and the service passes the value to the Controller.

When a request would go over the cap, the Controller asks the owner with `AskUserQuestion`. The options are: start fewer now, tear down idle peasants, or cancel. *(Claude's call.)*

## 7. Monitors

### PR Monitor

1. Calls `subscribe_pr_activity` on itself. Comments, reviews and CI results then arrive as events, the same mechanism as the web "auto-fix" toggle.
2. Fixes each problem, commits and pushes. It attaches the repo with push access if needed.
3. When the PR merges or closes, calls `unsubscribe_pr_activity`, reports, and goes idle for recycling.

### Slack Monitor

1. Wakes on a `create_trigger` schedule that targets itself and reads new channel messages through the Slack connector. `create_trigger` normally rejects schedules more frequent than hourly, so it runs hourly. *(Claude's call: the owner is still thinking this role through.)*
2. For each message worth answering, writes a suggestion in its own session: the question, avenues to investigate and likely fixes.
3. Never calls Slack send, draft or schedule tools. The owner is still thinking this role through.
4. **Teardown never depends on the Monitor obeying.** The Controller deletes the Monitor's trigger itself with `delete_trigger`, since triggers belong to the account, and then archives the session. *(Claude's call.)*

## 8. Memory

### Files

In `~/.fyrd/data/memory/`:
- `shared.md` is shared memory, which is also the Controller's memory.
- `roles/<role>.md` holds each role's memory, for example `roles/builder.md`.

The existing `owner-proxy.md`, `global.md` and per-repo files stay with the pipeline and are not used here. *(Claude's call.)*

### Writing

Any Fyrd session can contribute. When the owner tells a session to remember something ("Always ask before doing X"), the session writes:

````
```fyrd-remember
scope: role
Always ask before doing X.
```
````

- `scope: role` goes to the writer's role file. The service takes the role from the session's `fyrd-role-*` tag.
- `scope: shared` goes to `shared.md`.

The service only appends. Each entry gets the date and the source session's title.

### Capture

The service polls every live `fyrd-hub` session's events for `fyrd-remember` blocks in assistant messages. It keeps the last-seen event per session under `~/.fyrd/data/hub/` so that an entry is appended once.

### Delivery

- Peasants get `shared.md` and their role file inside `append_system_prompt` when they are created. The Controller copies them from its latest memory.
- Live sessions are not updated (owner's choice).

### Console

All memory is managed under **Settings › Agent memory** (§12). Entries are grouped by shared memory and by role, and each can be added, edited or deleted.

## 9. Asking the owner

Every persona, the Controller's and each peasant's, prefers the **ask-user flow** (the `AskUserQuestion` tool, with options) whenever something needs to happen or a decision has to be made (owner's rule). Plain-prose questions are a fallback, used only when the tool is unavailable.

Why: the console can then render the question's options as buttons.

- **Service:** while polling `fyrd-hub` sessions (§8), it also detects a pending `AskUserQuestion`: a tool use with no result yet, while the session is `blocked`. It shows the question as a console card with one button per option, plus free text for "Other".
- **Answer:** the console posts the owner's choice back to that session as the tool's answer. The owner can also answer in claude.ai directly. When an answer has arrived from either side, the card closes.
- **Open risk (needs a spike):** how an API client answers a pending `AskUserQuestion` (event shape and route) is not verified. If it can't be done, the console shows the question with a link to open the session, and the owner answers there.

## 10. Skills

The skills ship as **claude.ai account skills** (owner's choice). Every cloud session the owner runs can see them, so they are prefixed `fyrd-` and each one says it is for the Fyrd Controller only.

1. `fyrd-research`: start or recycle a Researcher for a uri.
2. `fyrd-build`: one Builder per uri, in parallel.
3. `fyrd-peasant`: create, recycle and tear down sessions, plus the naming, tags and settings rules from §6.
4. `fyrd-watch`: PR Monitor or Slack Monitor.

The source lives in this repo, for example under `skills/controller/` *(Claude's call)*, and is uploaded to claude.ai.

**Fallback** *(Claude's call)*: a spike may show that account skills can't be loaded in sessions created through `/v1/sessions`. In that case the service pastes the skill text into the Controller's `append_system_prompt`, as it already does for personas, and the skill files stay the source either way.

## 11. Hardening `controller.md`

1. Replace the reference to a nonexistent "/claude-code-remote skill" with the `mcp__claude-code-remote__*` tools.
2. Remove the `/docs/fyrd/context` repo storage.
3. Replace "reset the session's context and send … `append_system_prompt`" with recycle by archive and recreate (§6).
4. Replace `{{MEMORY}}` with the shared and role memory blocks (§6), and add the `fyrd-remember` rule.
5. Fill in the role list and the request table (§5), and point to the skills.
6. Add the ask-user rule (§9).

## 12. Console UI

The console follows the owner's mockup: the "fyrd" Design canvas at https://claude.ai/artifact/LyjRt8594EXGq4V8UsQn5E. It uses the **Anvil2** look and feel (ServiceTitan's design system; the canvas uses the `anvil2` light theme). Top navigation: **Inbox** (with a pending count), **Sessions**, **Settings**.

1. **Inbox.** One card per session waiting on the owner. Each card shows:
   - the session name, repo and age
   - the conversation so far, collapsible
   - the question, with one button per option (primary, default and danger tones) and a free-text reply box

   It is fed by pending `AskUserQuestion` calls (§9). When nothing is waiting, it shows "You're all caught up".
2. **Sessions.** A searchable list of `fyrd-hub` sessions, with the Controller pinned first. Each row shows status, repo, branch, PR state (draft, ready, merged or closed), check counts and age. The detail pane has:
   - the transcript, and a "Message this session" box that posts a real user turn
   - pause and resume
   - persona, model, estimated cost and budget, with "+$5 / +$25 / +$100" (**placeholders for now**; cost tracking and budgets are deferred, owner's decision)
   - tags, pull requests with checks, repository, start time, last activity and role
3. **Settings.**
   - **Environment:** the cloud environment new sessions start in.
   - **Session cap:** the global limit (§6), default 20.
   - **Personas:** a list with a model per persona and an editable definition. "Add persona" and "Delete" are there, but the Controller can't be deleted.
   - **Agent memory:** entries grouped by agent (shared and per role), newest first, each showing its source and date. You can add, edit and delete entries.

These three screens **replace** the current Inbox, Board and Memory tabs (owner's decision). The pipeline's server code stays in place until it is deprecated, but it no longer has a console UI.

## 13. Open risks

1. **Artifact inputs.** It is not verified that a cloud session can read a claude.ai artifact link, nor which tool it would use. Check this in the same spike as `AskUserQuestion` answers (§9).
2. **Uploading account skills.** It's unknown whether claude.ai has an API to upload them. If not, every skill edit is a manual upload.
3. **Cross-repo `add_repo`.** It is documented in the tool schema but not yet tried by a Fyrd peasant.
4. **Memory poisoning.** Any session can write memory, and a Slack Monitor reads untrusted text. The console shows each entry's source session so the owner can remove bad entries. The Monitor persona forbids writing memory from content it reads.
5. **Polling cost.** The service polls every live `fyrd-hub` session for memory blocks and pending questions. That is fine for a handful of sessions.

## 14. Left to the builder

- Prompt wording of personas and skills, within the rules above.
- How the service stores Controller state (session id, last-seen events) under `~/.fyrd/data/hub/`.
- Console layout for the Controller button and the new memory files.
- Test strategy, following the repo's existing patterns.
