import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import type { SessionSkill, SessionSkillSetup } from "../types";
import SessionSkillsCard, { MAX_SKILL_WORDS, shortHash, wordCount } from "./SessionSkillsCard";

vi.mock("../api");

const skills: SessionSkill[] = [
  { name: "fyrd-check-rules", description: "Check rules: use when a check fails.", roles: ["builder", "monitor"], body: "# Check rules\n\nEvent rules.", seedHash: "s1" },
  { name: "fyrd-stacked-pull-requests", description: "Stacked pull requests: use for a stack.", roles: ["builder"], body: "# Stacked pull requests\n\nStack rules.", seedHash: "s2" },
];
const setup = (o: Partial<SessionSkillSetup> = {}): SessionSkillSetup => ({
  mode: "inline", hash: "aaaaaaaaaaaa1111-current", confirmedHash: null, environmentId: "env_abc", skills: skills.map((s) => s.name),
  script: "# >>> fyrd session skills aaaaaaaaaaaa1111-current >>>\nSCRIPT\n# <<< fyrd session skills <<<\n",
  skillsDir: "/root/.claude/skills", hashFile: "/root/.claude/fyrd-session-skills/hash", ...o,
});

const H = "aaaaaaaaaaaa1111-current", H2 = "bbbbbbbbbbbb2222-new";
const confirmButton = () => screen.getByRole("button", { name: "I've pasted this script" });
const copyScript = async () => {
  await userEvent.click(screen.getByRole("button", { name: "Copy script" }));
  await screen.findByText("Copied.");
};

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(api.getSessionSkills).mockResolvedValue(skills);
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup());
  vi.mocked(api.saveSessionSkill).mockImplementation(async (s) => s);
  vi.mocked(api.deleteSessionSkill).mockResolvedValue(undefined);
});

const renderCard = async () => {
  render(<SessionSkillsCard />);
  await screen.findByRole("button", { name: /^fyrd-stacked-pull-requests/ });
  await screen.findByLabelText("Setup script");
};

it("lists the skills and shows inline mode with the script for the configured environment", async () => {
  await renderCard();
  expect(screen.getByRole("heading", { name: "Session skills" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: /^fyrd-check-rules/ })).toBeInTheDocument();
  const status = screen.getByRole("status", { name: "Session skills status" });
  expect(status).toHaveTextContent("Inline");
  expect(status).toHaveTextContent("Sessions get the whole text of every skill in their instructions.");
  expect(screen.getByLabelText("Setup script")).toHaveValue(setup().script);
  expect(screen.getByLabelText("Setup script")).toHaveAttribute("readonly");
  expect(screen.getByText(/setup script of environment env_abc at claude\.ai\/code/)).toBeInTheDocument();
  expect(screen.getByText(/It writes the skills to \/root\/\.claude\/skills/)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Stop using the script" })).not.toBeInTheDocument();
});

it("says where to paste when no environment is set", async () => {
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup({ environmentId: null }));
  await renderCard();
  expect(screen.getByText(/Fyrd has no environment set \(Settings, Environment\), so sessions start in the Controller's environment/)).toBeInTheDocument();
});

it("copies the script, and says so in a live region that is always there", async () => {
  const user = userEvent.setup();
  const writeText = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue();
  await renderCard();
  // The region is mounted before anything is copied, so a screen reader hears the text arrive.
  const live = screen.getAllByRole("status").find((e) => e.textContent === "");
  expect(live).toBeDefined();
  await user.click(screen.getByRole("button", { name: "Copy script" }));
  expect(writeText).toHaveBeenCalledWith(setup().script);
  expect(await screen.findByText("Copied.")).toBe(live);
});

it("says to copy by hand when the browser has no clipboard", async () => {
  await renderCard();
  const clipboard = Object.getOwnPropertyDescriptor(navigator, "clipboard");
  Object.defineProperty(navigator, "clipboard", { value: undefined, configurable: true });
  try {
    await userEvent.click(screen.getByRole("button", { name: "Copy script" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Could not copy. Select the script and copy it by hand.");
    expect(screen.queryByText("Copied.")).not.toBeInTheDocument();
    expect(confirmButton()).toBeDisabled();
  } finally {
    if (clipboard) Object.defineProperty(navigator, "clipboard", clipboard);
    else delete (navigator as { clipboard?: unknown }).clipboard;
  }
});

it("shows the start of the script's hash to compare with the block's first line", async () => {
  await renderCard();
  expect(shortHash(H)).toBe("aaaaaaaaaaaa");
  expect(screen.getByText(/This script's first line holds the hash that starts with/)).toHaveTextContent("aaaaaaaaaaaa.");
});

it("confirming after a copy records the hash of the script that was copied and switches to installed", async () => {
  vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue();
  vi.mocked(api.confirmSessionSkillSetup).mockResolvedValue(setup({ mode: "installed", confirmedHash: H }));
  await renderCard();
  // Nothing was copied in this tab yet: there is nothing to confirm.
  expect(confirmButton()).toBeDisabled();
  await copyScript();
  expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
  await userEvent.click(confirmButton());
  expect(api.confirmSessionSkillSetup).toHaveBeenCalledWith(H);
  const status = await screen.findByRole("status", { name: "Session skills status" });
  expect(status).toHaveTextContent("Installed");
  expect(status).toHaveTextContent("New sessions load these skills on demand from the environment's setup script.");
  expect(confirmButton()).toBeDisabled();
  expect(screen.getByRole("button", { name: "Stop using the script" })).toBeInTheDocument();
});

it("a script that changed after it was copied can't be confirmed until it is copied again", async () => {
  vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue();
  await renderCard();
  await copyScript();
  // The owner pastes the copied block, then edits a skill in this same tab: the script on screen is a new one.
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-stacked-pull-requests/ }));
  await userEvent.type(screen.getByLabelText("Body"), " More.");
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup({ hash: H2, script: "NEW SCRIPT" }));
  await userEvent.click(screen.getByRole("button", { name: "Save session skill" }));
  expect(await screen.findByDisplayValue("NEW SCRIPT")).toBeInTheDocument();
  const notice = screen.getByText("The script changed since you copied it").closest("[role=status]");
  expect(notice).toHaveTextContent("The block you copied starts with aaaaaaaaaaaa and is no longer the current one. Copy the script again, paste it over the old block, then confirm.");
  expect(confirmButton()).toBeDisabled();
  await userEvent.click(confirmButton());
  expect(api.confirmSessionSkillSetup).not.toHaveBeenCalled();

  await copyScript();
  expect(screen.queryByText("The script changed since you copied it")).not.toBeInTheDocument();
  vi.mocked(api.confirmSessionSkillSetup).mockResolvedValue(setup({ mode: "installed", confirmedHash: H2, hash: H2 }));
  await userEvent.click(confirmButton());
  expect(api.confirmSessionSkillSetup).toHaveBeenCalledWith(H2);
});

it("a script pasted in an earlier visit is confirmed only after the owner vouches for the hash on its first line", async () => {
  vi.mocked(api.confirmSessionSkillSetup).mockResolvedValue(setup({ mode: "installed", confirmedHash: H }));
  await renderCard();
  expect(screen.getByText(/To confirm, copy the script here and paste it, or tick the box above/)).toBeInTheDocument();
  const vouch = screen.getByRole("checkbox", {
    name: "I pasted this script without copying it here just now, and the first line of the block in the environment holds the hash that starts with aaaaaaaaaaaa",
  });
  expect(confirmButton()).toBeDisabled();
  await userEvent.click(vouch);
  expect(confirmButton()).toBeEnabled();
  await userEvent.click(confirmButton());
  expect(api.confirmSessionSkillSetup).toHaveBeenCalledWith(H);
});

it("vouching holds for one script only: a new script asks again", async () => {
  await renderCard();
  await userEvent.click(screen.getByRole("checkbox"));
  expect(confirmButton()).toBeEnabled();
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-stacked-pull-requests/ }));
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup({ hash: H2, script: "NEW SCRIPT" }));
  await userEvent.click(screen.getByRole("button", { name: "Save session skill" }));
  expect(await screen.findByDisplayValue("NEW SCRIPT")).toBeInTheDocument();
  expect(screen.getByRole("checkbox", { name: /starts with bbbbbbbbbbbb$/ })).not.toBeChecked();
  expect(confirmButton()).toBeDisabled();
});

it("a refused confirmation shows the error and the current script, which must be copied again", async () => {
  vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue();
  vi.mocked(api.confirmSessionSkillSetup).mockRejectedValue(new Error("The session skills changed after this script was made."));
  await renderCard();
  await copyScript();
  // The skills changed in another tab: this tab still shows the script it copied.
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup({ hash: H2, script: "NEW SCRIPT" }));
  await userEvent.click(confirmButton());
  expect(api.confirmSessionSkillSetup).toHaveBeenCalledWith(H);
  expect(await screen.findByRole("alert")).toHaveTextContent("The session skills changed after this script was made.");
  expect(await screen.findByDisplayValue("NEW SCRIPT")).toBeInTheDocument();
  expect(screen.getByText("The script changed since you copied it")).toBeInTheDocument();
  expect(confirmButton()).toBeDisabled();
});

it("confirm can't be sent twice while the first request is under way", async () => {
  vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue();
  let finish!: (s: SessionSkillSetup) => void;
  vi.mocked(api.confirmSessionSkillSetup).mockReturnValue(new Promise((r) => { finish = r; }));
  await renderCard();
  await copyScript();
  await userEvent.click(confirmButton());
  expect(confirmButton()).toBeDisabled();
  await userEvent.click(confirmButton());
  expect(api.confirmSessionSkillSetup).toHaveBeenCalledTimes(1);
  finish(setup({ mode: "installed", confirmedHash: H }));
  expect(await screen.findByRole("status", { name: "Session skills status" })).toHaveTextContent("Installed");
});

it("clearing goes back to inline", async () => {
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup({ mode: "installed", confirmedHash: "aaaaaaaaaaaa1111-current" }));
  vi.mocked(api.clearSessionSkillSetup).mockResolvedValue(setup());
  await renderCard();
  await userEvent.click(screen.getByRole("button", { name: "Stop using the script" }));
  expect(api.clearSessionSkillSetup).toHaveBeenCalled();
  expect(await screen.findByRole("status", { name: "Session skills status" })).toHaveTextContent("Inline");
});

it("an edit after the paste shows that sessions use the inline text until the new script is pasted", async () => {
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup({ mode: "installed", confirmedHash: "aaaaaaaaaaaa1111-current" }));
  await renderCard();
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-stacked-pull-requests/ }));
  // Sessions get the change only once the Controller has its new instructions.
  expect(screen.getByText(/Saving a change makes the pasted setup script out of date: sessions the Controller creates after it has taken its new instructions, which it gets the next time it is idle, get the inline text until you paste the new script and confirm\./)).toBeInTheDocument();
  expect(screen.getByText(/"Stop using the script" puts the whole text back into the instructions of the sessions the Controller creates after it has taken its new instructions, which it gets the next time it is idle\. Remove the block from the environment yourself, after that\./)).toBeInTheDocument();
  expect(screen.getByLabelText("Name")).toBeDisabled();
  const body = screen.getByLabelText("Body");
  await userEvent.clear(body);
  await userEvent.type(body, "New rules.");
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup({ mode: "out-of-date", confirmedHash: "aaaaaaaaaaaa1111-current", hash: "bbbbbbbbbbbb2222-new", script: "NEW SCRIPT" }));
  await userEvent.click(screen.getByRole("button", { name: "Save session skill" }));
  expect(api.saveSessionSkill).toHaveBeenCalledWith({
    name: "fyrd-stacked-pull-requests", description: "Stacked pull requests: use for a stack.", roles: ["builder"], body: "New rules.",
  });
  const notice = await screen.findByText("The setup script is out of date");
  expect(notice.closest("[role=status]")).toHaveTextContent("Sessions use the inline text until you paste the new script and confirm.");
  expect(screen.getByLabelText("Setup script")).toHaveValue("NEW SCRIPT");
});

it("adds a skill, checking its name, description, roles, body and word budget first", async () => {
  await renderCard();
  await userEvent.click(screen.getByRole("button", { name: "Add session skill" }));
  const save = () => userEvent.click(screen.getByRole("button", { name: "Save session skill" }));
  const alert = async () => (await screen.findByRole("alert")).textContent;
  const name = screen.getByLabelText("Name");
  expect(name).toHaveValue("fyrd-");
  await save();
  expect(await alert()).toContain("Name the skill fyrd- followed by lower-case words joined by hyphens");
  await userEvent.type(name, "Release Notes");
  await save();
  expect(await alert()).toContain("Name the skill fyrd-");
  await userEvent.clear(name);
  await userEvent.type(name, "fyrd-stacked-pull-requests");
  await save();
  expect(await alert()).toContain("A session skill with this name already exists.");
  await userEvent.clear(name);
  await userEvent.type(name, "fyrd-release-notes");
  await save();
  expect(await alert()).toContain("Give the skill a description");
  await userEvent.type(screen.getByLabelText("Description"), "Release notes: use when you write them.");
  const roles = screen.getByLabelText("Roles");
  expect(roles).toHaveValue("builder");
  await userEvent.clear(roles);
  await save();
  expect(await alert()).toContain("Name at least one role");
  await userEvent.type(roles, "builder, reviewer, builder");
  await save();
  expect(await alert()).toContain("Give the skill a body.");
  const body = screen.getByLabelText("Body");
  await userEvent.click(body);
  await userEvent.paste(Array(MAX_SKILL_WORDS + 1).fill("word").join(" "));
  expect(screen.getByText(`${MAX_SKILL_WORDS + 1} of ${MAX_SKILL_WORDS} words. Put the rules that matter most first.`)).toBeInTheDocument();
  await save();
  expect(await alert()).toContain(`The body has ${MAX_SKILL_WORDS + 1} words; a session skill holds at most ${MAX_SKILL_WORDS}.`);
  expect(api.saveSessionSkill).not.toHaveBeenCalled();
  await userEvent.clear(body);
  await userEvent.type(body, "Write them.");
  await save();
  expect(api.saveSessionSkill).toHaveBeenCalledWith({
    name: "fyrd-release-notes", description: "Release notes: use when you write them.", roles: ["builder", "reviewer"], body: "Write them.",
  });
});

it("shows a server refusal", async () => {
  vi.mocked(api.saveSessionSkill).mockRejectedValue(new Error("There is no persona 'nobody'."));
  await renderCard();
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-check-rules/ }));
  await userEvent.click(screen.getByRole("button", { name: "Save session skill" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("There is no persona 'nobody'.");
  expect(screen.getByLabelText("Name")).toHaveValue("fyrd-check-rules");
});

it("a skill with a newer default shows a chip; a seeded skill can be reset, one the owner made cannot", async () => {
  vi.mocked(api.getSessionSkills).mockResolvedValue([
    { ...skills[0], body: "Mine.", updateAvailable: true },
    { name: "fyrd-mine", description: "Mine: use it.", roles: ["builder"], body: "Mine.", seedHash: null },
  ]);
  vi.mocked(api.resetSessionSkill).mockResolvedValue(skills[0]);
  render(<SessionSkillsCard />);
  await userEvent.click(await screen.findByRole("button", { name: /^fyrd-mine/ }));
  expect(screen.queryByRole("button", { name: "Reset to default" })).not.toBeInTheDocument();
  expect(screen.getByText("Update available")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-check-rules/ }));
  expect(screen.getByText("A newer default is available for this skill.")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Reset to default" }));
  expect(api.resetSessionSkill).toHaveBeenCalledWith("fyrd-check-rules");
  expect(await screen.findByDisplayValue(/Event rules\./)).toBeInTheDocument();
});

it("says that a change reaches sessions only after the Controller has taken its new instructions", async () => {
  await renderCard();
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-stacked-pull-requests/ }));
  expect(screen.getByText("Changes apply to sessions the Controller creates after it has taken its new instructions, which it gets the next time it is idle.")).toBeInTheDocument();
});

it("the name of a skill with a newer default reads as two words", async () => {
  vi.mocked(api.getSessionSkills).mockResolvedValue([{ ...skills[0], updateAvailable: true }]);
  render(<SessionSkillsCard />);
  expect(await screen.findByRole("button", { name: "fyrd-check-rules Update available" })).toBeInTheDocument();
});

it("deletes a skill only after a confirmation that says which roles lose its rules", async () => {
  await renderCard();
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-check-rules/ }));
  await userEvent.click(screen.getByRole("button", { name: "Delete session skill" }));
  expect(api.deleteSessionSkill).not.toHaveBeenCalled();
  const ask = screen.getByRole("alertdialog", { name: "Delete session skill" });
  expect(ask).toHaveTextContent("Delete fyrd-check-rules?");
  expect(ask).toHaveTextContent("Sessions of the roles it belongs to (builder, monitor) lose these rules: nothing else in their instructions holds them. You can restore the default afterwards.");
  // Keeping it changes nothing.
  await userEvent.click(within(ask).getByRole("button", { name: "Keep it" }));
  expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  expect(api.deleteSessionSkill).not.toHaveBeenCalled();

  await userEvent.click(screen.getByRole("button", { name: "Delete session skill" }));
  await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Delete it" }));
  expect(api.deleteSessionSkill).toHaveBeenCalledWith("fyrd-check-rules");
  expect(await screen.findByText("Select a session skill to edit it.")).toBeInTheDocument();
});

it("a skill the owner made is deleted for good, and the confirmation says so", async () => {
  vi.mocked(api.getSessionSkills).mockResolvedValue([{ name: "fyrd-mine", description: "Mine: use it.", roles: ["builder"], body: "Mine.", seedHash: null }]);
  render(<SessionSkillsCard />);
  await userEvent.click(await screen.findByRole("button", { name: /^fyrd-mine/ }));
  await userEvent.click(screen.getByRole("button", { name: "Delete session skill" }));
  expect(screen.getByRole("alertdialog")).toHaveTextContent("lose these rules: nothing else in their instructions holds them. It can't be brought back.");
});

it("lists the defaults that were deleted and restores one", async () => {
  vi.mocked(api.getSessionSkills).mockResolvedValue([skills[0]]);
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup({ missingDefaults: ["fyrd-stacked-pull-requests"] }));
  vi.mocked(api.resetSessionSkill).mockResolvedValue(skills[1]);
  render(<SessionSkillsCard />);
  const deleted = await screen.findByRole("region", { name: "Deleted defaults" });
  expect(deleted).toHaveTextContent("Defaults you deleted. The roles they belong to are without these rules until you restore them.");
  expect(deleted).toHaveTextContent("fyrd-stacked-pull-requests");
  vi.mocked(api.getSessionSkills).mockResolvedValue(skills);
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup({ missingDefaults: [] }));
  await userEvent.click(within(deleted).getByRole("button", { name: "Restore default fyrd-stacked-pull-requests" }));
  expect(api.resetSessionSkill).toHaveBeenCalledWith("fyrd-stacked-pull-requests");
  expect(await screen.findByRole("button", { name: /^fyrd-stacked-pull-requests/ })).toBeInTheDocument();
  expect(screen.queryByRole("region", { name: "Deleted defaults" })).not.toBeInTheDocument();
});

it("shows no deleted defaults when there are none", async () => {
  await renderCard();
  expect(screen.queryByRole("region", { name: "Deleted defaults" })).not.toBeInTheDocument();
});

it("asks before another skill replaces a draft with changes that are not saved", async () => {
  await renderCard();
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-stacked-pull-requests/ }));
  // Without changes, another skill opens at once.
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-check-rules/ }));
  expect(screen.getByLabelText("Name")).toHaveValue("fyrd-check-rules");
  expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();

  await userEvent.type(screen.getByLabelText("Body"), " Unsaved.");
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-stacked-pull-requests/ }));
  const ask = screen.getByRole("alertdialog", { name: "Unsaved changes" });
  expect(ask).toHaveTextContent("Your changes to fyrd-check-rules are not saved");
  expect(screen.getByLabelText("Name")).toHaveValue("fyrd-check-rules");
  await userEvent.click(within(ask).getByRole("button", { name: "Keep editing" }));
  expect(screen.getByLabelText("Body")).toHaveValue("# Check rules\n\nEvent rules. Unsaved.");

  // "Add session skill" asks too; discarding opens what was asked for.
  await userEvent.click(screen.getByRole("button", { name: "Add session skill" }));
  await userEvent.click(within(screen.getByRole("alertdialog")).getByRole("button", { name: "Discard changes" }));
  expect(screen.getByLabelText("Name")).toHaveValue("fyrd-");
  expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
});

it("save, reset and delete can't be sent twice while a request is under way", async () => {
  let finish!: (s: SessionSkill) => void;
  vi.mocked(api.saveSessionSkill).mockReturnValue(new Promise((r) => { finish = r; }));
  await renderCard();
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-check-rules/ }));
  await userEvent.click(screen.getByRole("button", { name: "Save session skill" }));
  for (const name of ["Save session skill", "Reset to default", "Delete session skill"]) expect(screen.getByRole("button", { name })).toBeDisabled();
  await userEvent.click(screen.getByRole("button", { name: "Save session skill" }));
  expect(api.saveSessionSkill).toHaveBeenCalledTimes(1);
  finish(skills[0]);
  expect(await screen.findByText("Select a session skill to edit it.")).toBeInTheDocument();
});

it("opens the setup guide in place", async () => {
  vi.mocked(api.getSessionSkillGuide).mockResolvedValue({ text: "# Session skills\n\nPaste the block." });
  await renderCard();
  const toggle = screen.getByRole("button", { name: "Setup guide" });
  expect(toggle).toHaveAttribute("aria-expanded", "false");
  await userEvent.click(toggle);
  expect(toggle).toHaveAttribute("aria-expanded", "true");
  expect(document.getElementById(toggle.getAttribute("aria-controls")!)).toHaveAttribute("aria-label", "Setup guide");
  const guide = await screen.findByRole("region", { name: "Setup guide" });
  expect(within(guide).getByRole("heading", { name: "Session skills" })).toBeInTheDocument();
  expect(within(guide).getByText("Paste the block.")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Hide the setup guide" }));
  expect(screen.queryByRole("region", { name: "Setup guide" })).not.toBeInTheDocument();
});

it("counts words as the server does", () => {
  expect(wordCount("")).toBe(0);
  expect(wordCount("  one\ttwo\n\nthree  ")).toBe(3);
});
