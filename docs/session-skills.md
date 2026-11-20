# Session skills

A session skill is a part of a role's instructions that only some tasks need. The Builder's rules for stacked pull requests are one: most builds are a single pull request and never use them. Fyrd keeps such a part out of the role's persona and gives it to the session as a skill, which the session loads with the Skill tool when the situation comes up. Every session of that role then starts with shorter instructions.

Fyrd ships one session skill, for the Builder:

| Skill | Holds | A Builder loads it |
| --- | --- | --- |
| `stacked-pull-requests` (`fyrd:stacked-pull-requests` in a session) | How to build, open, fix, validate and move work in a native GitHub stack | when the work is, or may be, split into more than one pull request |

Only rules that some tasks never need belong in a session skill. Rules every task reaches, such as the Builder's rules for pull request events, stay in the persona: a skill that is always loaded saves nothing.

The defaults are skills of the fyrd Claude Code plugin: each is `skills/<name>/SKILL.md` in this repository, where Claude Code's plugin loader finds it, and a session that has the plugin lists it as `fyrd:<name>`. Their front matter also names `roles`, the personas whose sessions get the skill; a plugin skill without `roles` is no session skill.

You edit them in the console under **Settings › Session skills**, next to the personas, and you can add your own. Fyrd copies each default into its own data once, keeps a skill you never edited in step with its default (and removes it when a later version of Fyrd no longer ships that default), and leaves a skill you edited alone (the list then shows "Update available", and "Reset to default" takes the default).

Deleting a skill takes its rules away from the roles it belongs to: nothing else in their instructions holds them, so the console asks first. A default you deleted stays deleted; the list shows it under "Defaults you deleted" with **Restore default**.

## Two ways a session gets a skill

- **Inline.** The whole text of every skill is added to the persona text of its role. Nothing has to be set up, and nothing is saved either. This is how Fyrd starts.
- **Installed.** The persona text holds one line per skill (the name the session loads it by, `fyrd:<name>`, and its description), and the session loads the text from the fyrd plugin. This is what makes the instructions shorter.

Fyrd uses installed mode only when you have told it that the setup script below is in place. Even then, the plugin holds only the defaults as this repository ships them: a skill whose description or body you changed in the console, and a skill you added, is not in the plugin, so its whole text stays inline. A change of roles only is fine: the plugin doesn't hold the roles. **Reset to default** makes an edited skill the plugin's again. An edited default's inline section opens with a line saying it replaces the plugin skill of that name (`fyrd:<name>`) and that the session must not load that skill, since the plugin's unedited text is still in the session's skill list. Fyrd adds that line in inline mode too, where the plugin may be installed already.

Fyrd can't look into the environment, so it knows only what you confirmed. In installed mode a session's instructions tell it to stop and ask you when a skill they point at is not in its skill list (the block was never pasted, it failed, or the session started from an older snapshot). This check is an instruction the session follows, not something Fyrd enforces.

## Why a setup script

A cloud session starts from its environment. An environment has a setup script, which runs as root before Claude Code starts; the result is saved as a snapshot and reused by the sessions that start later. A plugin that the script installs for root's user scope is loaded in those sessions and in their subagents.

The repository is public, so the script needs nothing from Fyrd or your computer: it installs the plugin from GitHub with the Claude Code CLI. The script holds no skill text, so it is the same block whatever you change in the console, and you paste it once. A setup script can only be changed by hand in the environment's dialog at claude.ai/code, so pasting it is your step.

## Set it up

1. **Find the environment.** In the Fyrd console open **Settings › Environment** and read **Cloud environment id**. That is the environment Fyrd's sessions start in. If the field is empty, sessions start in the environment the Controller runs in. **Settings › Session skills** names the environment above the script.
2. **Copy the script.** In **Settings › Session skills**, under **Setup script**, click **Copy script**. The block starts with `# >>> fyrd plugin >>>` and ends with `# <<< fyrd plugin <<<`.
3. **Open the environment's dialog.** Go to [claude.ai/code](https://claude.ai/code) and open the dialog (the settings) of that environment. Its setup script is edited there, and nowhere else.
4. **Paste the block.**
   - If the script is empty, paste the block.
   - If the script has content of your own, keep it and add the block (before or after it; the block doesn't depend on the rest and can't stop it).
   - If the script has a Fyrd block from an earlier version, from its `# >>> fyrd session skills … >>>` line to its `# <<< fyrd session skills <<<` line, replace that block with the new one. Keep one Fyrd block only.
5. **Save** the environment.
6. **Confirm in the console.** Back in **Settings › Session skills**, click **I've pasted this script**. The status changes to **Installed**. Sessions the Controller creates from now on get the short persona text. The Controller gets its new instructions the next time it is idle, so a session it creates in the moment between may still get the inline text.

Confirm only after you saved the environment. If you confirm without the script in place, a Builder that needs a skill doesn't find it in its skill list. Its instructions tell it to stop and ask you. Paste the script, or click **Stop using the script**.

When you change **Cloud environment id**, Fyrd drops the confirmation: it was for the old environment. Paste the block into the new environment and confirm.

## What the block does

```sh
claude plugin marketplace add nhuizenga/fyrd
claude plugin marketplace update fyrd
claude plugin install fyrd@fyrd --scope user
claude plugin update fyrd@fyrd
```

- It adds this repository as the Claude Code plugin marketplace `fyrd` and installs the plugin `fyrd@fyrd` for root's user scope: the CLI clones the repository under `/root/.claude/plugins/` and records the marketplace and the plugin in `/root/.claude/settings.json` (`extraKnownMarketplaces` and `enabledPlugins`), which a session's Claude Code reads when it starts.
- It is safe to run again: a marketplace or plugin that is already there is kept, and the `update` lines bring both to the repository's latest version.
- It runs in a subshell that ends in `|| true` and has no `exit`: a failed command (no network, say) can't fail or end the rest of your script. When the `claude` command is not on the PATH it says so on stderr and installs nothing.
- `CLAUDE_CONFIG_DIR`, Claude Code's own variable, moves where the CLI writes; that is only for trying the block out on your own computer. A cloud environment doesn't set it.

## After you edit a skill

The block stays as it is: there is nothing to paste again. Sessions the Controller creates after it has taken its new instructions get your edited skill's whole text, inline, beside the one-line pointers to the skills that are still the plugin's. The console names those skills in the status line, and `/fyrd:status` lists them.

## Check it in a session

In a session created after you saved the environment:

- its skill list holds `fyrd:stacked-pull-requests` (ask it "which skills do you have?");
- `claude plugin list` shows `fyrd@fyrd`, enabled.

`/fyrd:status` prints the mode in one line: `inline` or `installed`.

## Turn it off

1. In **Settings › Session skills**, click **Stop using the script**. Fyrd goes back to inline mode. The Controller gets its new instructions the next time it is idle, so a session it creates while it is still busy is still pointed at the plugin's skills.
2. Remove the Fyrd block from the environment's setup script only after the Controller has taken the new instructions (Fyrd sends them as a message to the Controller, so they appear in the Controller's conversation). Until you remove it, sessions still have the plugin, and nothing points at its skills.

## Limits

- **The paste is manual.** There is no API for an environment's setup script, so Fyrd can't install, update or check it. Fyrd knows only what you confirmed. What guards against a missing plugin is the session's own check of its skill list, which is an instruction it follows, not a guarantee.
- **Snapshot cache.** The environment's file system is saved after the setup script runs and reused, so sessions get the plugin as the repository was when the snapshot was built. A newer Fyrd reaches sessions when the environment builds a new snapshot (it does when you change the setup script). Until then a session can load an older version of a default than the one your console shows.
- **Every session of the environment.** The plugin is installed for the whole environment, so every session there lists its skills and its `/fyrd:` commands, whatever its role; only the roles a skill names are pointed at it.
- **Changes reach sessions through the Controller.** A change (an edit, a confirmation, turning it off) is in the instructions Fyrd sends the Controller the next time it is idle. A session the Controller creates before that gets the earlier text.
- **New sessions only.** A setup script runs when a session's environment is built. A session that is already running keeps the plugin it started with, and the persona text it started with.
- **Size.** A skill body holds at most 1,500 words. After a session's context is compacted it keeps only the first 5,000 tokens of each loaded skill, so put the rules that matter most first.
- **Subagent texts.** The cut of a persona that another session runs as a subagent (`<subagent-persona>`) carries no session skills.
