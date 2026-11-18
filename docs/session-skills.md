# Session skills

A session skill is a part of a role's instructions that only some tasks need. The Builder's rules for stacked pull requests are one: most builds are a single pull request and never use them. Fyrd keeps such a part out of the role's persona and gives it to the session as a skill, which the session loads with the Skill tool when the situation comes up. Every session of that role then starts with shorter instructions.

Fyrd ships two session skills, both for the Builder:

| Skill | Holds | A Builder loads it |
| --- | --- | --- |
| `fyrd-stacked-pull-requests` | How to build, open, fix, validate and move work in a native GitHub stack | when the work is, or may be, split into more than one pull request |
| `fyrd-pull-request-events` | What to do with reviews, review comments, failing checks, merges and closes that arrive as events | before it posts its review, and when a pull request event arrives |

You edit them in the console under **Settings › Session skills**, next to the personas, and you can add your own. The files under `skills/session/` in this repository are only the defaults: Fyrd copies each one into its own data once, keeps a skill you never edited in step with its default, and leaves a skill you edited alone (the list then shows "Update available", and "Reset to default" takes the default).

## Two ways a session gets a skill

- **Inline.** The whole text of every skill is added to the persona text of its role. Nothing has to be set up, and nothing is saved either. This is how Fyrd starts.
- **Installed.** The persona text holds one line per skill (its name and its description), and the text itself sits in the session's skills folder. This is what makes the instructions shorter.

Fyrd uses installed mode only when you have told it that the setup script below is in place, and only while that script matches the skills as they are now. In every other case it uses inline mode, so a session is never pointed at a skill that isn't there or is out of date.

## Why a setup script

A cloud session starts from its environment. An environment has a setup script, which runs as root before Claude Code starts; the result is saved as a snapshot and reused by the sessions that start later. A skill that the script writes to `/root/.claude/skills/<name>/SKILL.md` is listed in those sessions and in their subagents.

The setup script can't reach Fyrd, your computer or a private repository, so the script has to hold the skills' text itself. Fyrd generates that script from the skills. A setup script can only be changed by hand in the environment's dialog at claude.ai/code, so pasting it is your step.

## Set it up

1. **Find the environment.** In the Fyrd console open **Settings › Environment** and read **Cloud environment id**. That is the environment Fyrd's sessions start in. If the field is empty, sessions start in the environment the Controller runs in. **Settings › Session skills** names the environment above the script.
2. **Copy the script.** In **Settings › Session skills**, under **Setup script**, click **Copy script**. The block starts with `# >>> fyrd session skills <hash> >>>` and ends with `# <<< fyrd session skills <<<`.
3. **Open the environment's dialog.** Go to [claude.ai/code](https://claude.ai/code) and open the dialog (the settings) of that environment. Its setup script is edited there, and nowhere else.
4. **Paste the block.**
   - If the script is empty, paste the block.
   - If the script has content of your own, keep it and add the block (before or after it; the block doesn't depend on the rest and can't stop it).
   - If the script already has a Fyrd block, replace everything from its `# >>> fyrd session skills … >>>` line to its `# <<< fyrd session skills <<<` line with the new block. Keep one Fyrd block only.
5. **Save** the environment.
6. **Confirm in the console.** Back in **Settings › Session skills**, click **I've pasted this script**. The status changes to **Installed**. Sessions the Controller creates from now on get the short persona text. The Controller gets its new instructions the next time it is idle, so a session it creates in the moment between may still get the inline text.

Confirm only after you saved the environment. If you confirm without the script in place, a Builder that needs a skill won't find it. Its instructions tell it to stop and ask you; nothing is done without the rules. Paste the script, or click **Stop using the script**.

## What the block does

- It writes each skill to `/root/.claude/skills/<name>/SKILL.md`, with only `name` and `description` in the front matter.
- It writes the names it installed to `/root/.claude/fyrd-session-skills/manifest`. On a later run it removes a skill that an earlier block listed there and that no longer exists. It never removes anything else.
- It writes the hash from its first line to `/root/.claude/fyrd-session-skills/hash`, last, as the sign of a complete run.
- It uses no network, runs in a subshell that ends in `|| true`, and has no `exit`: it can't fail or end the rest of your script. Running it twice gives the same files.

## After you edit a skill

Any change to a session skill in the console (an edit, a new skill, a deleted one, a reset) makes the pasted script out of date. From that moment:

- the status shows **The setup script is out of date**, and `/fyrd:status` says `Session skills: out of date`;
- sessions created from then on get the **inline** text, with your change, so the change takes effect at once and nothing depends on the old script;
- the console shows the new script. Paste it over the old block (step 4) and click **I've pasted this script** again to go back to installed mode.

The same happens when you change **Cloud environment id**: the confirmation was for the old environment, so Fyrd drops it. Paste the block into the new environment and confirm.

## Check it in a session

In a session created after you saved the environment:

- its skill list holds `fyrd-stacked-pull-requests` and `fyrd-pull-request-events` (ask it "which skills do you have?");
- `cat /root/.claude/fyrd-session-skills/hash` prints the hash in the first line of the block you pasted, which is also the hash the console shows in the current script's first line;
- `ls /root/.claude/skills` shows one folder per skill.

`/fyrd:status` prints the mode in one line: `inline`, `installed` or `out of date`.

## Turn it off

1. In **Settings › Session skills**, click **Stop using the script**. Fyrd goes back to inline mode at once.
2. Remove the Fyrd block from the environment's setup script when you like. Until you do, the skill folders are still written, and nothing points at them.

## Limits

- **The paste is manual.** There is no API for an environment's setup script, so Fyrd can't install, update or check it. Fyrd knows only what you confirmed.
- **New sessions only.** A setup script runs when a session's environment is built. A session that is already running keeps the files it started with, and the persona text it started with.
- **Snapshot cache.** The environment's file system is saved after the setup script runs and reused. Changing the script makes the environment build a new snapshot; a session that starts from an older snapshot has the older skills. If a new session lacks the skills, check the hash file as above.
- **Size.** A skill body holds at most 1,500 words. After a session's context is compacted it keeps only the first 5,000 tokens of each loaded skill, so put the rules that matter most first.
- **Root's home.** The block writes under `/root/.claude`, where a cloud session's Claude Code looks. It is not meant for your own computer.
- **Subagent texts.** The cut of a persona that another session runs as a subagent (`<subagent-persona>`) carries no session skills.
