import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { config, hubState } from "../test/fixtures";
import { useRoute } from "../route";
import { getItemLimit } from "../useTranscript";
import type { HubMemory, HubPersona, HubState, PendingMemoryEntry } from "../types";
import type { ScreenProps } from "./ScreenProps";
import SettingsScreen from "./SettingsScreen";

vi.mock("../api");

const personas: HubPersona[] = [
  { id: "controller", name: "Controller", model: "claude-opus-5-5", defaultModel: "claude-opus-5-5", definition: "Control.", locked: true },
  { id: "builder", name: "Builder", model: "claude-sonnet-5-5", defaultModel: "claude-sonnet-5-5", definition: "Build things.", locked: false },
];
const memory: HubMemory = {
  scopes: [
    { scope: "builder", label: "Builder", entries: [{ id: "e2", text: "Builder note", source: "agent", at: "t" }] },
    { scope: "shared", label: "Shared", entries: [{ id: "e1", text: "Shared note", source: "owner", at: "t" }] },
  ],
};

/** SettingsScreen as the app mounts it: the section comes from the `#/settings/{section}` route. */
function Routed(props: ScreenProps) {
  const { screen: current, id } = useRoute();
  return <SettingsScreen {...props} routeId={current === "settings" ? id : null} />;
}

const waiting: PendingMemoryEntry[] = [
  { id: "p1", text: "Always squash.", source: "builder-1", sessionId: "s1", at: new Date().toISOString() },
];

beforeEach(() => {
  window.location.hash = "";
  vi.resetAllMocks();
  vi.mocked(api.getConfig).mockResolvedValue(config());
  vi.mocked(api.getPersonas).mockResolvedValue(personas);
  vi.mocked(api.getHubMemory).mockResolvedValue(memory);
  vi.mocked(api.getPendingMemory).mockResolvedValue([]);
  vi.mocked(api.approvePendingMemory).mockResolvedValue({ id: "n", text: "x", source: "builder-1", at: "t" });
  vi.mocked(api.rejectPendingMemory).mockResolvedValue(undefined);
  vi.mocked(api.updateConfig).mockResolvedValue(config() as never);
  vi.mocked(api.setPersonaModel).mockImplementation(async (id, model) => ({ ...personas.find((p) => p.id === id)!, model }));
  vi.mocked(api.addMemory).mockResolvedValue({ id: "n", text: "x", source: "owner", at: "t" });
  vi.mocked(api.editMemory).mockResolvedValue(undefined);
  vi.mocked(api.deleteMemory).mockResolvedValue(undefined);
});

const renderSettings = async (section: "Environment" | "Spend limits" | "Display" | "Personas" | "Agent memory" = "Personas", state = hubState()) => {
  render(<Routed state={state} onChanged={vi.fn()} />);
  await userEvent.click(screen.getByRole("button", { name: section }));
  if (section === "Personas") await screen.findByRole("gridcell", { name: "Builder" }, { timeout: 10000 }); // the table loads lazily
  else if (section === "Agent memory") await screen.findByText("Shared note");
};

it("settings sections are a left nav showing one section at a time", async () => {
  render(<Routed state={hubState()} onChanged={vi.fn()} />);
  const nav = screen.getByRole("navigation", { name: "Settings sections" });
  expect(nav).toHaveTextContent("EnvironmentSpend limitsDisplayPersonasSession skillsAgent memory");
  expect(screen.getByRole("heading", { name: "Environment" })).toBeInTheDocument();
  expect(screen.queryByRole("heading", { name: "Personas" })).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Agent memory" }));
  expect(screen.getByRole("heading", { name: "Agent memory" })).toBeInTheDocument();
  expect(screen.queryByRole("heading", { name: "Environment" })).not.toBeInTheDocument();
});

it("session skills are a settings section next to the personas", async () => {
  vi.mocked(api.getSessionSkills).mockResolvedValue([]);
  vi.mocked(api.getSessionSkillSetup).mockResolvedValue({
    mode: "inline", confirmed: false, environmentId: null, skills: [], script: "SCRIPT", repository: "nhuizenga/fyrd",
  });
  render(<Routed state={hubState()} onChanged={vi.fn()} />);
  await userEvent.click(screen.getByRole("button", { name: "Session skills" }));
  expect(window.location.hash).toBe("#/settings/session-skills");
  expect(screen.getByRole("heading", { name: "Session skills" })).toBeInTheDocument();
  expect(await screen.findByDisplayValue("SCRIPT")).toBeInTheDocument();
});

it("environment save calls updateConfig", async () => {
  await renderSettings("Environment");
  const field = screen.getByLabelText("Cloud environment id");
  await userEvent.type(field, "env_123");
  await userEvent.click(screen.getByRole("button", { name: "Save environment" }));
  expect(api.updateConfig).toHaveBeenCalledWith({ environmentId: "env_123" });
});

it("session cap rejects 0 and saves 20", async () => {
  await renderSettings("Environment");
  const field = screen.getByLabelText("Session cap");
  await userEvent.clear(field);
  await userEvent.type(field, "0");
  await userEvent.click(screen.getByRole("button", { name: "Save session cap" }));
  expect(api.updateConfig).not.toHaveBeenCalled();
  expect(screen.getByText("Enter a whole number from 1 to 100.")).toBeInTheDocument();
  await userEvent.clear(field);
  await userEvent.type(field, "20");
  await userEvent.click(screen.getByRole("button", { name: "Save session cap" }));
  expect(api.updateConfig).toHaveBeenCalledWith({ sessionCap: 20 });
});

/** The persona table's rows, as [persona, model, default] cell text. */
const personaRows = () =>
  within(screen.getByRole("grid")).getAllByRole("row").slice(1)
    .map((r) => within(r).getAllByRole("gridcell").map((c) => c.textContent?.replace(/Press Enter.*$/, "").replace(/Reset$/, "").trim()));

it("personas are a table of name, model and default, with no prompt or name editing", async () => {
  await renderSettings();
  expect(personaRows()).toEqual([
    ["Controller", "Opus 5.5", "Opus 5.5"],
    ["Builder", "Sonnet 5.5", "Sonnet 5.5"],
  ]);
  for (const gone of ["Add persona", "Save persona", "Delete persona"])
    expect(screen.queryByRole("button", { name: gone })).not.toBeInTheDocument();
  expect(screen.queryByLabelText("Definition")).not.toBeInTheDocument();
  expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: /Reset .* to the default model/ })).not.toBeInTheDocument();
});

it("picking a model in the table saves only the model", async () => {
  await renderSettings();
  const row = screen.getAllByRole("row").find((r) => within(r).queryByRole("gridcell", { name: "Builder" }))!;
  await userEvent.click(within(row).getAllByRole("gridcell")[1]);
  expect(screen.getAllByRole("option").map((o) => o.textContent)).toEqual([
    "Opus 5.5 · claude-opus-5-5", "Sonnet 5.5 · claude-sonnet-5-5", "Haiku 5.5 · claude-haiku-5-5", "Fable 5.2 · claude-fable-5-2", "Fable 5.1 · claude-fable-5-1",
  ]);
  await userEvent.click(screen.getByRole("option", { name: "Haiku 5.5 · claude-haiku-5-5" }));
  expect(api.setPersonaModel).toHaveBeenCalledWith("builder", "claude-haiku-5-5");
  expect(await screen.findByText("Builder now uses Haiku 5.5.")).toBeInTheDocument();
  expect(personaRows()[1]).toEqual(["Builder", "Haiku 5.5", "Sonnet 5.5"]);
});

it("a persona on another model than its default can be reset", async () => {
  vi.mocked(api.getPersonas).mockResolvedValue([personas[0], { ...personas[1], model: "claude-opus-5-5" }]);
  vi.mocked(api.resetPersona).mockResolvedValue(personas[1]);
  await renderSettings();
  await userEvent.click(screen.getByRole("button", { name: "Reset Builder to the default model" }));
  expect(api.resetPersona).toHaveBeenCalledWith("builder");
  expect(await screen.findByText("Builder now uses Sonnet 5.5.")).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Reset Builder to the default model" })).not.toBeInTheDocument();
});

it("a failed model change shows the error and reloads the table", async () => {
  vi.mocked(api.setPersonaModel).mockRejectedValue(new Error("Unknown model 'x'."));
  await renderSettings();
  const row = screen.getAllByRole("row").find((r) => within(r).queryByRole("gridcell", { name: "Builder" }))!;
  await userEvent.click(within(row).getAllByRole("gridcell")[1]);
  await userEvent.click(screen.getByRole("option", { name: "Haiku 5.5 · claude-haiku-5-5" }));
  expect(await screen.findByText("Unknown model 'x'.")).toBeInTheDocument();
  expect(api.getPersonas).toHaveBeenCalledTimes(2);
});

it("memory groups shared first; add, edit, delete entry", async () => {
  await renderSettings("Agent memory");
  const headings = (await screen.findAllByRole("heading", { level: 4 })).map((h) => h.textContent);
  expect(headings.indexOf("Shared")).toBeLessThan(headings.indexOf("Builder"));

  await userEvent.type(screen.getByLabelText("New memory entry"), "Remember this");
  await userEvent.click(screen.getByRole("button", { name: "Add entry" }));
  expect(api.addMemory).toHaveBeenCalledWith("shared", "Remember this");

  await userEvent.click(screen.getAllByRole("button", { name: "Edit" })[0]);
  const edit = screen.getByLabelText("Edit memory entry");
  await userEvent.clear(edit);
  await userEvent.type(edit, "Changed");
  await userEvent.click(screen.getByRole("button", { name: "Save" }));
  expect(api.editMemory).toHaveBeenCalledWith("shared", "e1", "Changed");

  // No confirmation step: the trash icon deletes at once.
  await userEvent.click(screen.getAllByRole("button", { name: "Delete" })[0]);
  expect(api.deleteMemory).toHaveBeenCalledWith("shared", "e1");
});

it("memory scope picker hides the Controller (its memory is Shared)", async () => {
  vi.mocked(api.getHubMemory).mockResolvedValue({
    scopes: [...memory.scopes, { scope: "controller", label: "Controller", entries: [] }],
  });
  await renderSettings("Agent memory");
  await userEvent.click(screen.getByRole("combobox", { name: "Memory scope" }));
  expect(await screen.findByRole("option", { name: "Builder" })).toBeInTheDocument();
  expect(screen.queryByRole("option", { name: "Controller" })).not.toBeInTheDocument();
});

it("changes apply note", async () => {
  await renderSettings("Agent memory");
  expect(screen.getAllByText("Changes apply to sessions created from now on.").length).toBeGreaterThan(0);
});

it("a section has its own deep link", async () => {
  window.location.hash = "#/settings/memory";
  render(<Routed state={hubState()} onChanged={vi.fn()} />);
  expect(screen.getByRole("heading", { name: "Agent memory" })).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Personas" }));
  expect(window.location.hash).toBe("#/settings/personas");
});

it("spend limits load, validate and save both limits", async () => {
  vi.mocked(api.getConfig).mockResolvedValue(config({ sessionDailyLimitUsd: 40, fyrdDailyLimitUsd: 400 }));
  await renderSettings("Spend limits");
  const s = await screen.findByLabelText("Per-session daily limit (USD)");
  await vi.waitFor(() => expect(s).toHaveValue("40"));
  expect(screen.getByLabelText("Fyrd-wide daily limit (USD)")).toHaveValue("400");
  expect(screen.getAllByText("Whole dollars, 1 to 10000.")).toHaveLength(2);
  await userEvent.clear(s);
  await userEvent.type(s, "0");
  await userEvent.click(screen.getByRole("button", { name: "Save session limit" }));
  expect(api.updateConfig).not.toHaveBeenCalled();
  expect(screen.getByText("Enter a whole number from 1 to 10000.")).toBeInTheDocument();
  await userEvent.clear(s);
  await userEvent.type(s, " 75 ");
  await userEvent.click(screen.getByRole("button", { name: "Save session limit" }));
  expect(api.updateConfig).toHaveBeenCalledWith({ sessionDailyLimitUsd: 75 });
  expect(await screen.findByText("Session limit saved.")).toBeInTheDocument();
  const f = screen.getByLabelText("Fyrd-wide daily limit (USD)");
  await userEvent.clear(f);
  await userEvent.type(f, "10001");
  await userEvent.click(screen.getByRole("button", { name: "Save fyrd limit" }));
  expect(api.updateConfig).toHaveBeenCalledTimes(1);
  await userEvent.clear(f);
  await userEvent.type(f, "900");
  await userEvent.click(screen.getByRole("button", { name: "Save fyrd limit" }));
  expect(api.updateConfig).toHaveBeenCalledWith({ fyrdDailyLimitUsd: 900 });
  expect(await screen.findByText("Fyrd limit saved.")).toBeInTheDocument();
});

it("display: the messages-per-conversation limit loads, validates and saves to this browser", async () => {
  localStorage.clear();
  await renderSettings("Display");
  const field = screen.getByLabelText("Messages shown per conversation");
  expect(field).toHaveValue("100");
  await userEvent.clear(field);
  await userEvent.type(field, "5");
  await userEvent.click(screen.getByRole("button", { name: "Save message limit" }));
  expect(screen.getByText("Enter a whole number from 10 to 5000.")).toBeInTheDocument();
  expect(getItemLimit()).toBe(100);
  await userEvent.clear(field);
  await userEvent.type(field, " 250 ");
  await userEvent.click(screen.getByRole("button", { name: "Save message limit" }));
  expect(await screen.findByText(/Message limit saved/)).toBeInTheDocument();
  expect(getItemLimit()).toBe(250);
  localStorage.clear();
});

const budget = (o: Partial<NonNullable<HubState["budget"]>> = {}) => ({
  date: "2026-11-01", timeZone: "America/Chicago", fyrdCostToday: 123.456, fyrdLimitUsd: 500, fyrdBlocked: false, ...o,
});

it("spend limits show today's total spend against the Fyrd-wide limit", async () => {
  vi.mocked(api.getConfig).mockResolvedValue(config());
  await renderSettings("Spend limits", hubState({ budget: budget() }));
  const total = await screen.findByRole("status", { name: "Total spend today" });
  expect(total).toHaveTextContent("$123.46 of $500");
  expect(total).toHaveTextContent("25%");
  expect(total).toHaveTextContent("2026-11-01");
  expect(screen.queryByText("Limit reached")).not.toBeInTheDocument();
});

it("the total spend status says when the Fyrd-wide limit is reached", async () => {
  vi.mocked(api.getConfig).mockResolvedValue(config());
  await renderSettings("Spend limits", hubState({ budget: budget({ fyrdCostToday: 512.5, fyrdBlocked: true }) }));
  const total = await screen.findByRole("status", { name: "Total spend today" });
  expect(total).toHaveTextContent("$512.50 of $500");
  expect(total).toHaveTextContent("Limit reached");
});

it("the total spend status copes with no spend data yet", async () => {
  vi.mocked(api.getConfig).mockResolvedValue(config());
  await renderSettings("Spend limits", hubState({ budget: null }));
  expect(await screen.findByRole("status", { name: "Total spend today" })).toHaveTextContent("No spend data yet");
});

it("pending shared memory: approve, edit then approve, reject", async () => {
  vi.mocked(api.getPendingMemory).mockResolvedValue(waiting);
  const onChanged = vi.fn();
  render(<Routed state={hubState()} onChanged={onChanged} />);
  await userEvent.click(screen.getByRole("button", { name: "Agent memory" }));
  expect(await screen.findByRole("heading", { name: "Waiting for your approval" })).toBeInTheDocument();
  expect(screen.getByText("Always squash.")).toBeInTheDocument();
  expect(screen.getByText(/From builder-1/)).toBeInTheDocument();

  const group = within(screen.getByRole("heading", { name: "Waiting for your approval" }).parentElement!);
  await userEvent.click(group.getByRole("button", { name: "Approve" }));
  expect(api.approvePendingMemory).toHaveBeenCalledWith("p1");
  expect(onChanged).toHaveBeenCalled();

  await userEvent.click(group.getByRole("button", { name: "Edit" }));
  const box = screen.getByLabelText("Edit proposed memory");
  await userEvent.clear(box);
  await userEvent.type(box, "Squash merges only.");
  await userEvent.click(group.getByRole("button", { name: "Save & approve" }));
  expect(api.approvePendingMemory).toHaveBeenCalledWith("p1", "Squash merges only.");

  await userEvent.click(group.getByRole("button", { name: "Reject" }));
  expect(api.rejectPendingMemory).toHaveBeenCalledWith("p1");
});

it("pending group is hidden when nothing is waiting", async () => {
  await renderSettings("Agent memory");
  expect(screen.queryByRole("heading", { name: "Waiting for your approval" })).not.toBeInTheDocument();
});

it("pending list reloads when the polled count changes while Settings is open", async () => {
  const { rerender } = render(<Routed state={hubState({ pendingMemory: 0 })} onChanged={vi.fn()} />);
  await userEvent.click(screen.getByRole("button", { name: "Agent memory" }));
  await screen.findByText("Shared note");
  expect(screen.queryByText("Always squash.")).not.toBeInTheDocument();

  vi.mocked(api.getPendingMemory).mockResolvedValue(waiting);
  rerender(<Routed state={hubState({ pendingMemory: 1 })} onChanged={vi.fn()} />);
  expect(await screen.findByText("Always squash.")).toBeInTheDocument();
});

it("pending buttons are disabled while a request is in flight", async () => {
  vi.mocked(api.getPendingMemory).mockResolvedValue(waiting);
  let finish: () => void = () => {};
  vi.mocked(api.approvePendingMemory).mockReturnValue(new Promise((resolve) => {
    finish = () => resolve({ id: "n", text: "x", source: "builder-1", at: "t" });
  }));
  render(<Routed state={hubState()} onChanged={vi.fn()} />);
  await userEvent.click(screen.getByRole("button", { name: "Agent memory" }));
  const group = within((await screen.findByRole("heading", { name: "Waiting for your approval" })).parentElement!);

  await userEvent.click(group.getByRole("button", { name: "Approve" }));
  expect(group.getByRole("button", { name: "Approve" })).toBeDisabled();
  expect(group.getByRole("button", { name: "Reject" })).toBeDisabled();
  await userEvent.click(group.getByRole("button", { name: "Reject" }));
  expect(api.rejectPendingMemory).not.toHaveBeenCalled();
  finish();
});

it("optional keys: saving sends the value, never shows it back, and a saved one can be removed", async () => {
  const view = (source: "saved" | "none") => ({ supported: true, secrets: [
    { name: "anthropic-api-key" as const, set: source === "saved", source },
    { name: "github-token" as const, set: false, source: "none" as const },
  ] });
  vi.mocked(api.getSecrets).mockResolvedValue(view("none"));
  vi.mocked(api.saveSecret).mockResolvedValue(view("saved"));
  vi.mocked(api.removeSecret).mockResolvedValue(view("none"));
  await renderSettings("Environment");
  const field = await screen.findByLabelText("Anthropic API Key (optional)");
  expect(field).toHaveAttribute("type", "password");
  expect(screen.getByLabelText("GitHub Token (optional)")).toBeInTheDocument();
  await userEvent.type(field, "sk-ant-secret");
  await userEvent.click(screen.getByRole("button", { name: "Save Anthropic API Key" }));
  expect(api.saveSecret).toHaveBeenCalledWith("anthropic-api-key", "sk-ant-secret");
  expect(field).toHaveValue("");
  await userEvent.click(await screen.findByRole("button", { name: "Remove saved Anthropic API Key" }));
  expect(api.removeSecret).toHaveBeenCalledWith("anthropic-api-key");
});
