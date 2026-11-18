import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import type { SessionSkill, SessionSkillSetup } from "../types";
import SessionSkillsCard, { MAX_SKILL_WORDS, wordCount } from "./SessionSkillsCard";

vi.mock("../api");

const skills: SessionSkill[] = [
  { name: "fyrd-pull-request-events", description: "Pull request events: use when an event arrives.", roles: ["builder"], body: "# Pull request events\n\nEvent rules.", seedHash: "s1" },
  { name: "fyrd-stacked-pull-requests", description: "Stacked pull requests: use for a stack.", roles: ["builder"], body: "# Stacked pull requests\n\nStack rules.", seedHash: "s2" },
];
const setup = (o: Partial<SessionSkillSetup> = {}): SessionSkillSetup => ({
  mode: "inline", hash: "h-current", confirmedHash: null, environmentId: "env_abc", skills: skills.map((s) => s.name),
  script: "# >>> fyrd session skills h-current >>>\nSCRIPT\n# <<< fyrd session skills <<<\n",
  skillsDir: "/root/.claude/skills", hashFile: "/root/.claude/fyrd-session-skills/hash", ...o,
});

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
  expect(screen.getByRole("button", { name: /^fyrd-pull-request-events/ })).toBeInTheDocument();
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

it("copies the script", async () => {
  const user = userEvent.setup();
  const writeText = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue();
  await renderCard();
  await user.click(screen.getByRole("button", { name: "Copy script" }));
  expect(writeText).toHaveBeenCalledWith(setup().script);
  expect(await screen.findByText("Copied.")).toBeInTheDocument();
});

it("confirming the paste records the hash of the script shown and switches to installed", async () => {
  vi.mocked(api.confirmSessionSkillSetup).mockResolvedValue(setup({ mode: "installed", confirmedHash: "h-current" }));
  await renderCard();
  await userEvent.click(screen.getByRole("button", { name: "I've pasted this script" }));
  expect(api.confirmSessionSkillSetup).toHaveBeenCalledWith("h-current");
  const status = await screen.findByRole("status", { name: "Session skills status" });
  expect(status).toHaveTextContent("Installed");
  expect(status).toHaveTextContent("New sessions load these skills on demand from the environment's setup script.");
  expect(screen.getByRole("button", { name: "I've pasted this script" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Stop using the script" })).toBeInTheDocument();
});

it("a refused confirmation shows the error and the current script", async () => {
  vi.mocked(api.confirmSessionSkillSetup).mockRejectedValue(new Error("The session skills changed after this script was made."));
  await renderCard();
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup({ hash: "h-new", script: "NEW SCRIPT" }));
  await userEvent.click(screen.getByRole("button", { name: "I've pasted this script" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("The session skills changed after this script was made.");
  expect(await screen.findByDisplayValue("NEW SCRIPT")).toBeInTheDocument();
});

it("clearing goes back to inline", async () => {
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup({ mode: "installed", confirmedHash: "h-current" }));
  vi.mocked(api.clearSessionSkillSetup).mockResolvedValue(setup());
  await renderCard();
  await userEvent.click(screen.getByRole("button", { name: "Stop using the script" }));
  expect(api.clearSessionSkillSetup).toHaveBeenCalled();
  expect(await screen.findByRole("status", { name: "Session skills status" })).toHaveTextContent("Inline");
});

it("an edit after the paste shows that sessions use the inline text until the new script is pasted", async () => {
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup({ mode: "installed", confirmedHash: "h-current" }));
  await renderCard();
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-stacked-pull-requests/ }));
  expect(screen.getByText(/Saving a change makes the pasted setup script out of date/)).toBeInTheDocument();
  expect(screen.getByLabelText("Name")).toBeDisabled();
  const body = screen.getByLabelText("Body");
  await userEvent.clear(body);
  await userEvent.type(body, "New rules.");
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue(setup({ mode: "out-of-date", confirmedHash: "h-current", hash: "h-new", script: "NEW SCRIPT" }));
  await userEvent.click(screen.getByRole("button", { name: "Save session skill" }));
  expect(api.saveSessionSkill).toHaveBeenCalledWith({
    name: "fyrd-stacked-pull-requests", description: "Stacked pull requests: use for a stack.", roles: ["builder"], body: "New rules.",
  });
  const notice = await screen.findByText("The setup script is out of date");
  expect(notice.closest("[role=status]")).toHaveTextContent("Sessions use the inline text until you paste the new script and confirm.");
  expect(screen.getByLabelText("Setup script")).toHaveValue("NEW SCRIPT");
  // The new script can be confirmed.
  vi.mocked(api.confirmSessionSkillSetup).mockResolvedValue(setup({ mode: "installed", confirmedHash: "h-new", hash: "h-new" }));
  await userEvent.click(screen.getByRole("button", { name: "I've pasted this script" }));
  expect(api.confirmSessionSkillSetup).toHaveBeenCalledWith("h-new");
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
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-pull-request-events/ }));
  await userEvent.click(screen.getByRole("button", { name: "Save session skill" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("There is no persona 'nobody'.");
  expect(screen.getByLabelText("Name")).toHaveValue("fyrd-pull-request-events");
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
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-pull-request-events/ }));
  expect(screen.getByText("A newer default is available for this skill.")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Reset to default" }));
  expect(api.resetSessionSkill).toHaveBeenCalledWith("fyrd-pull-request-events");
  expect(await screen.findByDisplayValue(/Event rules\./)).toBeInTheDocument();
});

it("deletes a skill", async () => {
  await renderCard();
  await userEvent.click(screen.getByRole("button", { name: /^fyrd-stacked-pull-requests/ }));
  await userEvent.click(screen.getByRole("button", { name: "Delete session skill" }));
  expect(api.deleteSessionSkill).toHaveBeenCalledWith("fyrd-stacked-pull-requests");
  expect(await screen.findByText("Select a session skill to edit it.")).toBeInTheDocument();
});

it("opens the setup guide in place", async () => {
  vi.mocked(api.getSessionSkillGuide).mockResolvedValue({ text: "# Session skills\n\nPaste the block." });
  await renderCard();
  await userEvent.click(screen.getByRole("button", { name: "Setup guide" }));
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
