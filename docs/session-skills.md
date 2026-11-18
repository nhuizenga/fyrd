# Session skills

A session skill is a part of a role's instructions that only some tasks need. The Builder's rules for stacked pull requests are one: most builds are a single pull request and never use them. Fyrd keeps such a part out of the role's persona and gives it to the session as a skill, which the session loads with the Skill tool when the situation comes up. Every session of that role then starts with shorter instructions.

Fyrd ships one session skill, for the Builder:

| Skill | Holds | A Builder loads it |
| --- | --- | --- |
| `fyrd-stacked-pull-requests` | How to build, open, fix, validate and move work in a native GitHub stack | when the work is, or may be, split into more than one pull request |

Only rules that some tasks never need belong in a session skill. Rules every task reaches, such as the Builder's rules for pull request events, stay in the persona: a skill that is always loaded saves nothing.

You edit them in the console under **Settings › Session skills**, next to the personas, and you can add your own. The files under `skills/session/` in this repository are only the defaults: Fyrd copies each one into its own data once, keeps a skill you never edited in step with its default (and removes it when a later version of Fyrd no longer ships that default), and leaves a skill you edited alone (the list then shows "Update available", and "Reset to default" takes the default).

Deleting a skill takes its rules away from the roles it belongs to: nothing else in their instructions holds them, so the console asks first. A default you deleted stays deleted; the list shows it under "Defaults you deleted" with **Restore default**.

## Two ways a session gets a skill

- **Inline.** The whole text of every skill is added to the persona text of its role. Nothing has to be set up, and nothing is saved either. This is how Fyrd starts.
- **Installed.** The persona text holds one line per skill (its name and its description), and the text itself sits in the session's skills folder. This is what makes the instructions shorter.

Fyrd uses installed mode only when you have told it that the setup script below is in place, and only while that script matches the skills as they are now. In every other case it uses inline mode.

Fyrd can't look into the environment, so it knows only what you confirmed. That is why a session in installed mode checks for itself: its instructions hold the hash you confirmed, and tell it to check once, before it loads its first skill, that `/root/.claude/fyrd-session-skills/hash` holds exactly that hash. If the file is missing or holds another hash (the block was never pasted, an older block is still there, the block failed part-way, or the session started from an older snapshot), or a skill is not in its skill list, it stops and asks you. This check is an instruction the session follows, not something Fyrd enforces.

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
6. **Confirm in the console.** Back in **Settings › Session skills**, click **I've pasted this script**. Fyrd records the script you copied, not the one on screen: if a skill changed after you copied (the console then says "The script changed since you copied it"), copy and paste again first. If you pasted the block in an earlier visit, or copied it by hand, tick the box that says the first line of the block in the environment holds the hash shown, then confirm. The status changes to **Installed**. Sessions the Controller creates from now on get the short persona text. The Controller gets its new instructions the next time it is idle, so a session it creates in the moment between may still get the inline text.

Confirm only after you saved the environment. If you confirm without the script in place, a Builder that needs a skill finds no hash file, or no skill. Its instructions tell it to stop and ask you. Paste the script, or click **Stop using the script**.

## What the block does

- It writes each skill to `/root/.claude/skills/<name>/SKILL.md`, with only `name` and `description` in the front matter.
- It writes the names it installed to `/root/.claude/fyrd-session-skills/manifest`. On a later run it removes a skill that an earlier block listed there and that no longer exists. It never removes anything else.
- It removes `/root/.claude/fyrd-session-skills/hash` first, and writes the hash from its first line there last, only when every step before it succeeded. The hash file is the sign of a complete run: a run that failed anywhere leaves none, not even an older one.
- It uses no network, runs in a subshell that ends in `|| true`, and has no `exit`: it can't fail or end the rest of your script. Running it twice gives the same files, also when your script sets `-C` (noclobber).
- `FYRD_CLAUDE_DIR` is only for trying the block out: when that variable is set, the block writes there and not to `/root/.claude`. A cloud environment doesn't set it.

## After you edit a skill

Any change to a session skill in the console (an edit, a new skill, a deleted one, a reset) makes the pasted script out of date. From that moment:

- the status shows **The setup script is out of date**, and `/fyrd:status` says `Session skills: out of date`;
- sessions the Controller creates after it has taken its new instructions get the **inline** text, with your change, and nothing depends on the old script. The Controller gets its new instructions the next time it is idle, so a session it creates while it is still busy gets the earlier text: the short one that points at the old script's skills. Such a session finds the old hash it was told to expect and uses the skills as they were before your change;
- the console shows the new script. Paste it over the old block (step 4) and click **I've pasted this script** again to go back to installed mode.

The same happens when you change **Cloud environment id**: the confirmation was for the old environment, so Fyrd drops it. Paste the block into the new environment and confirm.

## Check it in a session

In a session created after you saved the environment:

- its skill list holds `fyrd-stacked-pull-requests` (ask it "which skills do you have?");
- `cat /root/.claude/fyrd-session-skills/hash` prints the hash in the first line of the block you pasted, which is also the hash the console shows in the current script's first line;
- `ls /root/.claude/skills` shows one folder per skill.

`/fyrd:status` prints the mode in one line: `inline`, `installed` or `out of date`.

## Turn it off

1. In **Settings › Session skills**, click **Stop using the script**. Fyrd goes back to inline mode. The Controller gets its new instructions the next time it is idle, so a session it creates while it is still busy is still pointed at the skills in the environment.
2. Remove the Fyrd block from the environment's setup script only after the Controller has taken the new instructions (Fyrd sends them as a message to the Controller, so they appear in the Controller's conversation). Until you remove it, the skill folders are still written, and nothing points at them.

## Limits

- **The paste is manual.** There is no API for an environment's setup script, so Fyrd can't install, update or check it. Fyrd knows only what you confirmed. What guards against a missing or older block is the session's own check of the hash file, which is an instruction it follows, not a guarantee.
- **Changes reach sessions through the Controller.** A change (an edit, a confirmation, turning it off) is in the instructions Fyrd sends the Controller the next time it is idle. A session the Controller creates before that gets the earlier text.
- **New sessions only.** A setup script runs when a session's environment is built. A session that is already running keeps the files it started with, and the persona text it started with.
- **Snapshot cache.** The environment's file system is saved after the setup script runs and reused. Changing the script makes the environment build a new snapshot; a session that starts from an older snapshot has the older skills. A session in installed mode checks the hash file before it loads a skill and asks you when it differs; you can check it yourself as above.
- **Size.** A skill body holds at most 1,500 words. After a session's context is compacted it keeps only the first 5,000 tokens of each loaded skill, so put the rules that matter most first.
- **Root's home.** The block writes under `/root/.claude`, where a cloud session's Claude Code looks. It is not meant for your own computer.
- **Subagent texts.** The cut of a persona that another session runs as a subagent (`<subagent-persona>`) carries no session skills.
