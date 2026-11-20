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

Personas are managed in the console under **Settings › Personas** (owner's decision). Each has a name, a model and a definition. They are stored by the service in `~/.fyrd/data/hub/personas/*.json` (one file per persona, so each can be edited and deleted on its own), not in a repo.

*Since then (owner's decision, model only):* a persona's name and text always come from its skill in the repo (`skills/<id>/SKILL.md`); **Settings › Personas** is a table where only each persona's model changes, and `~/.fyrd/data/hub/personas/<id>.json` holds just that model override. See `docs/controller-skills.md`.

- **Seeding:** this work adds the six personas to Settings: Controller, Researcher, Builder, Reviewer, Validator and Monitor. Their starting text comes from the owner's `personas/controller.md` (hardened per §11) and from new drafts. Builder is adapted from `implementer.md`. The pipeline's persona files are not used here. *(Claude's call: the seed text is checked in, for example under `personas/hub/`, and copied into `~/.fyrd` only when a persona doesn't exist there yet.)*
- **Model:** `create_session` gets the persona's model.
- **Deletion:** the Controller can't be deleted.
- **Changes** apply to sessions created afterwards. A change to the Controller persona applies after "Replace Controller" and creating a new one.

Each peasant persona tells the session to:
1. Read its inputs. They are GitHub file URLs or claude.ai artifact links (owner's decision). Then do the task it was given, and attach any repo it lacks with `add_repo` (`access: "push"` when it must commit), clone it, then call `register_repo_root`.
2. Act on messages from other Fyrd sessions as work instructions, but never as the owner's approval. *(Decision reversed 2026-11-02: the original design ruled out any trust boundary, because it would make leads steering their own peasants harder. A builder then called the owner's own console message "the peer's", and a Controller relay could have carried a forged "the owner approved". So peasants still do the work a lead sends them; only owner approval is withheld. Plain user turns are the owner, wrapped cross-session messages are not, and approvals need a plain owner turn or an `AskUserQuestion` answer.)*
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

> **Superseded (2026-11-17, PR #52).** Fyrd now creates the Controller itself through the owner's signed-in `claude` CLI (`claude --cloud controller` in a pseudo terminal, with the new session id read from its output, then the whole opening prompt sent with `claude -p --cloud <id>` on stdin), and the paste-an-id adopt flow below is removed. See `skills/controller/README.md`. The adoption notes are kept as history.

> **Adopted, not created (2026-10-31).** Sessions Fyrd creates through the API get no claude.ai connectors (its OAuth client's `mcp_connector_ids` are dropped), so a Fyrd-made Controller would have no `claude-code-remote` tools and couldn't create sessions. A session made by hand on claude.ai has every connector (hub spike, "Connectors"). So the owner creates the Controller and Fyrd adopts it. **To confirm in the smoke run:** that peasants the Controller creates with `create_session` inherit its connectors.

- **Owner creates, console adopts.** The owner creates a no-repo session on claude.ai and pastes its id into the console's "Adopt Controller" panel. Fyrd rejects an id that is not live or that carries Fyrd tags (`fyrd-hub`, `fyrd-role-*`, i.e. a peasant). It archives any previous Controller and stores the id. "Open Controller" opens the stored session while it is live. "Replace Controller" archives it and clears the id, for example after persona or skill changes; the owner then adopts a fresh session.
- **Its instructions arrive as the first message**, not as `append_system_prompt`. On adoption the service sends a user message that starts with "You are the Fyrd Controller." and holds:
  - `controller.md` and `<hub-settings>`
  - every peasant persona's text, so the Controller can pass each one to `create_session`
  - the subagent text of the validator, reviewer and investigator personas (see Delivery below)
  - shared memory and every role's memory, each in its own delimited block
- **No Fyrd tags.** Fyrd can't tag a session it didn't create, so the poll fetches the Controller by its stored id and treats it as a hub session (role `controller`).
- **Memory updates:** when a memory file changes, the service posts the changed memory to the Controller as a user message. The Controller uses the latest version for every peasant it creates after that.
- **Only one:** Fyrd stores one Controller id, and adopting another archives the previous one. The persona also tells the Controller to refuse to create another Controller.

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

In `~/.fyrd/data/hub/memory/`, one JSON file per scope (`<scope>.json`):
- `shared.json` is shared memory, which is also the Controller's memory. The Controller always writes `scope: shared`.
- `<role>.json` holds each role's memory, for example `builder.json`.

*(As built: JSON per scope rather than markdown, so the console can edit and delete single entries, each with its own id, source and date.)*

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
- `scope: shared` goes to `shared.json`.

The service only appends. Each entry gets the date and the source session's title.

### Capture

The service polls every live `fyrd-hub` session's events for `fyrd-remember` blocks in assistant messages. It keeps the last-seen event per session under `~/.fyrd/data/hub/` so that an entry is appended once.

### Delivery

- Peasants get `shared.md` and their role file inside `append_system_prompt` when they are created. The Controller copies them from its latest memory.
- Live sessions are not updated (owner's choice).
- **Subagent persona text.** A Builder runs the validator, reviewer and investigator personas as subagents in its own session, a Researcher the reviewer, a Validator and a Monitor the investigator. Their `append_system_prompt` carries those personas in `<subagent-persona id="…">` blocks, which the coordinator pastes into the subagent's brief. The blocks hold a cut of the persona, not all of it: the opening paragraph, the sections that describe the work and its findings (validator: The check; reviewer: Reviewing code for a Builder, Reviewing a plan for a Researcher, and in both the rule that a review changes and posts nothing; investigator: The investigation and As a subagent), and from Working in Fyrd the Inputs rule and the Untrusted content rule (owner's decision). The investigator persona has an Untrusted content rule, so its cut requires it; the validator and reviewer personas have none, so their cuts keep one only if the owner adds it. A subagent has no workers, no owner turn and no memory, so the sections about those would only cost context. The service makes the cut by heading from the stored persona when it builds the Controller message and sends it as `<subagent-persona-text>` blocks (the reviewer's two marked `for="builder"` and `for="researcher"`), so Settings, seeding and the seed hashes still work on whole personas. If a part the cut needs is gone (in Settings, a heading or one of those bullets renamed, the investigator's Untrusted content rule removed, or the reviewer's rule that a review changes and posts nothing moved out of Reviewing a plan or reworded at its start), that block holds the whole persona instead: a longer brief, never a missing rule. It does too when a heading the cut keeps appears twice, because a cut would hold only one of the two sections. A kept bullet is cut whole: an unindented line directly under it (a hard-wrapped bullet) and indented text after blank lines are part of it, and it ends at the next top-level bullet or at an unindented line after a blank line. Code fences are read as CommonMark does (three or more backticks or tildes, closed only by a fence of the same character that is at least as long), except that indentation is ignored, so a `# ` line inside one is not a heading. A persona whose definition is empty gets no block. The whole personas stay in the message too, because the owner still starts standalone Validators, Reviewers and Investigators (owner's decision).

### Console

All memory is managed under **Settings › Agent memory** (§12). Entries are grouped by shared memory and by role, and each can be added, edited or deleted.

## 9. Asking the owner

Every persona, the Controller's and each peasant's, prefers the **ask-user flow** (the `AskUserQuestion` tool, with options) whenever something needs to happen or a decision has to be made (owner's rule). Plain-prose questions are a fallback, used only when the tool is unavailable.

Why: the console can then render the question's options as buttons.

- **Service:** while polling `fyrd-hub` sessions (§8), it also detects a pending `AskUserQuestion`: a tool use with no result yet, while the session is `blocked`. It shows the question as a console card with one button per option, plus free text for "Other".
- **Answer:** the console posts the owner's choice back to that session as the tool's answer. The owner can also answer in claude.ai directly. When an answer has arrived from either side, the card closes.
- **Open risk (needs a spike):** how an API client answers a pending `AskUserQuestion` (event shape and route) is not verified. If it can't be done, the console shows the question with a link to open the session, and the owner answers there.

## 10. Skills

The request skills are skills of the fyrd plugin: `skills/<name>/SKILL.md` with `kind: controller-skill` in the front matter, loaded as `fyrd:<name>`, and each description says it is for the Fyrd Controller only, because the plugin is installed environment-wide. The service reads them and puts them in the Controller message: when the owner confirmed that the environment installs the plugin (§10, Session skills), as one `<controller-skills>` block with a line per skill that the Controller loads with the Skill tool; otherwise **inline**, each one a `<controller-skill name="fyrd:<name>">` block with its whole body. Nothing is uploaded to claude.ai. (Earlier versions shipped them as claude.ai account skills, then inline from `skills/controller/fyrd-<name>/`; the plugin replaced that.)

1. `fyrd:research`: start or recycle a Researcher for a uri.
2. `fyrd:build`: one Builder per uri, in parallel.
3. `fyrd:peasant`: create, recycle and tear down sessions, plus the naming, tags and settings rules from §6.
4. `fyrd:watch`: PR Monitor or Slack Monitor.
5. `fyrd:investigate`: an Investigator for a failing run, an incident, a suspected bug or a finding.
6. `fyrd:tune`: start the Tuner, or have it review a pull request or stack.

The source lives in this repo under `skills/<name>/`; the personas live there too, as plugin skills with `kind: persona` (`skills/controller/SKILL.md` is the Controller persona), and seed Settings from there instead of `personas/hub/`. The service reads the files on every poll pass. When the Controller's instructions (definition, settings, personas or skills) change, it re-sends the full Controller message while the Controller is idle; the message says the latest instructions replace earlier ones. Hashing of the instructions part (`PostedInstructionsHash`) works like the memory hash. Inline delivery is now the primary path, not a fallback.

### Session skills

A session skill is part of a role's instructions that only some tasks need. It is kept out of the role's persona, and a session of that role loads it with the Skill tool when its situation comes up, so every session starts with shorter instructions. The guide for the owner is `docs/session-skills.md`.

- **What one is.** A name (lower-case words joined by hyphens; a session loads it as `fyrd:<name>`), a one-line description (what it holds and when to load it), the roles it belongs to (persona ids, never the Controller) and a markdown body of at most 1,500 words. After compaction a session keeps only the first 5,000 tokens of a loaded skill, hence the limit.
- **Store.** Session skills are edited in the console under **Settings › Session skills**; the repo files are only seeds, as for personas (owner's decision). They are stored in `~/.fyrd/data/hub/session-skills/*.json` and seeded from the fyrd plugin's skills, `skills/<name>/SKILL.md`, whose front matter names `roles` (a plugin skill without `roles` is no session skill), by the persona rules: a seed is copied once, an unedited skill follows its seed, an edited one is left alone and flagged, a deleted one stays deleted (the setup endpoint lists it as a missing default, which the console can restore), and "Reset to default" takes the seed. An unedited skill whose seed is removed from the repo is removed with it; an edited one stays as the owner's own. Every stored skill records the hash of the seed it came from, so there is no seed-history file.
- **The seed.** `stacked-pull-requests` (`fyrd:stacked-pull-requests` in a session), for the Builder. It holds what used to be the Builder persona's Stacked pull requests section, plus the stack-only sentences of its steps 3 and 5, of Build rules and of Pull request hygiene (Moved work), word for word. The persona keeps every rule a Builder needs on every task, Pull request events among them (every build reaches review and gets events, so that section is no skill), and still names the stack section where it applies. It was `fyrd-stacked-pull-requests` under `skills/session/`; a store's skill is carried over to the new name.
- **Delivery through the plugin.** A cloud environment's setup script runs as root before Claude Code starts, and its result is a snapshot later sessions reuse; a plugin installed there for root's user scope is loaded in new sessions and their subagents. The repo is public, so the block (`SessionSkillScript`) installs the fyrd plugin from it with the Claude Code CLI (`claude plugin marketplace add`, `install fyrd@fyrd --scope user`, and an `update` of each): one subshell ending in `|| true`, no `exit`, a note on stderr and nothing installed when `claude` is not on the PATH. It holds no skill text, so it is the same block whatever the skills are and is pasted once. The environment is the one set in Fyrd's settings (owner's decision). A setup script can only be edited by hand in the environment's dialog, so the owner pastes the block; pasting by hand is accepted, with an onboarding guide (owner's decision). (Earlier versions generated a no-network block that wrote each skill's text and a hash file; installing the plugin replaced that.)
- **Two modes, with inline as the fallback.** When the service builds the Controller message it appends a role's session skills to that role's `<peasant-persona>` text, which the Controller copies as it is into a session's `append_system_prompt`. *Installed:* a `# Session skills` section that says to load a skill before acting on what it covers and to stop and ask the owner when it is not in the skill list, with one line per skill the plugin ships unchanged (`fyrd:<name>` and its description). The service can't see into an environment, so this check by the session is the only one of what the environment holds. A skill whose description or body was edited in the console, or one the owner added, is not in the plugin, so its whole body stays inline in either mode; a change of roles only keeps the pointer. *Inline:* each skill's whole body as a section, which is what a session was told before session skills existed. Installed mode needs only the owner's confirmation that the block is pasted, the `sessionSkillsPlugin` setting; a change of the environment id clears it. A change reaches new sessions once the Controller has its new instructions, which are sent the next time it is idle. The subagent persona texts carry no session skills.
- **API and CLI.** `/api/hub/session-skills` lists, saves, resets and deletes skills; `/api/hub/session-skills/setup-script` returns the block, the mode (`inline` or `installed`), whether it is confirmed, and which skills sessions load from the plugin and which stay inline; `POST` and `DELETE` on `/setup-script/confirm` record and clear the confirmation; `/api/hub/personas/{id}/prompt` returns the persona text a session of that role gets now. `/fyrd:status` prints the mode in one line.

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
   - persona, model, estimated cost and budget, with "+$25 / +$100" raises (estimated spend today against a daily limit; see the cost estimation and limits plan)
   - tags, pull requests with checks, repository, start time, last activity and role
3. **Settings.**
   - **Environment:** the cloud environment new sessions start in.
   - **Session cap:** the global limit (§6), default 20.
   - **Personas:** a list with a model per persona and an editable definition. "Add persona" and "Delete" are there, but the Controller can't be deleted. *(Since replaced by a table of personas where only the model changes; see the note under Personas above.)*
   - **Session skills:** the list of session skills with an editor (description, roles, body with its word count), "Add session skill", "Delete" (after a confirmation that names the roles losing the rules), "Reset to default" and "Restore default" for a deleted default; the status (inline, installed, or out of date with a notice that sessions use the inline text until the new script is pasted); the setup script with "Copy script", "I've pasted this script" and "Stop using the script"; and the setup guide (§10).
   - **Agent memory:** entries grouped by agent (shared and per role), newest first, each showing its source and date. You can add, edit and delete entries.

These three screens **replace** the current Inbox, Board and Memory tabs (owner's decision). The pipeline's server code stays in place until it is deprecated, but it no longer has a console UI.

## 13. Open risks

1. **Artifact inputs.** It is not verified that a cloud session can read a claude.ai artifact link, nor which tool it would use. Check this in the same spike as `AskUserQuestion` answers (§9).
2. **Uploading account skills.** Not needed: skills are delivered inline (§10). Account skills uploaded earlier should be deleted so no stale copy exists.
3. **Cross-repo `add_repo`.** It is documented in the tool schema but not yet tried by a Fyrd peasant.
4. **Memory poisoning.** Any session can write memory, and a Slack Monitor reads untrusted text. The console shows each entry's source session so the owner can remove bad entries. The Monitor persona forbids writing memory from content it reads.
5. **Polling cost.** The service polls every live `fyrd-hub` session for memory blocks and pending questions. That is fine for a handful of sessions.

## 14. Left to the builder

- Prompt wording of personas and skills, within the rules above.
- How the service stores Controller state (session id, last-seen events) under `~/.fyrd/data/hub/`.
- Console layout for the Controller button and the new memory files.
- Test strategy, following the repo's existing patterns.
