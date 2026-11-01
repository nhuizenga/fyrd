import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { config, hubState } from "../test/fixtures";
import { useRoute } from "../route";
import type { HubMemory, HubPersona, HubState } from "../types";
import type { ScreenProps } from "./ScreenProps";
import SettingsScreen from "./SettingsScreen";

vi.mock("../api");

const personas: HubPersona[] = [
  { id: "controller", name: "Controller", model: "claude-opus-5-5", definition: "Control.", locked: true },
  { id: "builder", name: "Builder", model: "claude-sonnet-5-5", definition: "Build things.", locked: false },
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

beforeEach(() => {
  window.location.hash = "";
  vi.resetAllMocks();
  vi.mocked(api.getConfig).mockResolvedValue(config());
  vi.mocked(api.getPersonas).mockResolvedValue(personas);
  vi.mocked(api.getHubMemory).mockResolvedValue(memory);
  vi.mocked(api.updateConfig).mockResolvedValue(config() as never);
  vi.mocked(api.savePersona).mockImplementation(async (p) => p);
  vi.mocked(api.addMemory).mockResolvedValue({ id: "n", text: "x", source: "owner", at: "t" });
  vi.mocked(api.editMemory).mockResolvedValue(undefined);
  vi.mocked(api.deleteMemory).mockResolvedValue(undefined);
  vi.mocked(api.deletePersona).mockResolvedValue(undefined);
});

const renderSettings = async (section: "Environment" | "Spend limits" | "Personas" | "Agent memory" = "Personas", state = hubState()) => {
  render(<Routed state={state} onChanged={vi.fn()} />);
  await userEvent.click(screen.getByRole("button", { name: section }));
  if (section === "Personas") await screen.findByRole("button", { name: /^Builder/ });
  else if (section === "Agent memory") await screen.findByText("Shared note");
};

it("settings sections are a left nav showing one section at a time", async () => {
  render(<Routed state={hubState()} onChanged={vi.fn()} />);
  const nav = screen.getByRole("navigation", { name: "Settings sections" });
  expect(nav).toHaveTextContent("EnvironmentSpend limitsPersonasAgent memory");
  expect(screen.getByRole("heading", { name: "Environment" })).toBeInTheDocument();
  expect(screen.queryByRole("heading", { name: "Personas" })).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Agent memory" }));
  expect(screen.getByRole("heading", { name: "Agent memory" })).toBeInTheDocument();
  expect(screen.queryByRole("heading", { name: "Environment" })).not.toBeInTheDocument();
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

it("personas list, edit model and definition, save", async () => {
  await renderSettings();
  await userEvent.click(screen.getByRole("button", { name: /^Builder/ }));
  await userEvent.click(screen.getByRole("combobox", { name: "Claude model" }));
  expect(screen.getAllByRole("option").map((o) => o.textContent)).toEqual([
    "Opus 5.5 · claude-opus-5-5", "Sonnet 5.5 · claude-sonnet-5-5", "Haiku 5.5 · claude-haiku-5-5", "Fable 5.1 · claude-fable-5-1",
  ]);
  await userEvent.click(screen.getByRole("option", { name: "Haiku 5.5 · claude-haiku-5-5" }));
  const def = screen.getByLabelText("Definition");
  await userEvent.clear(def);
  await userEvent.type(def, "New def");
  await userEvent.click(screen.getByRole("button", { name: "Save persona" }));
  expect(api.savePersona).toHaveBeenCalledWith({
    id: "builder", name: "Builder", model: "claude-haiku-5-5", definition: "New def", locked: false,
  });
});

it("controller delete disabled", async () => {
  await renderSettings();
  await userEvent.click(screen.getByRole("button", { name: /^Controller/ }));
  expect(screen.getByRole("button", { name: "Delete persona" })).toBeDisabled();
  expect(screen.getByText("The Controller persona can't be deleted")).toBeInTheDocument();
});

it("deletes a normal persona", async () => {
  await renderSettings();
  await userEvent.click(screen.getByRole("button", { name: /^Builder/ }));
  await userEvent.click(screen.getByRole("button", { name: "Delete persona" }));
  expect(api.deletePersona).toHaveBeenCalledWith("builder");
});

it("add persona", async () => {
  await renderSettings();
  await userEvent.click(screen.getByRole("button", { name: "Add persona" }));
  await userEvent.type(screen.getByLabelText("Name"), "Doc Writer");
  await userEvent.type(screen.getByLabelText("Definition"), "Write docs.");
  await userEvent.click(screen.getByRole("button", { name: "Save persona" }));
  expect(api.savePersona).toHaveBeenCalledWith({
    id: "doc-writer", name: "Doc Writer", model: "claude-sonnet-5-5", definition: "Write docs.", locked: false,
  });
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

it("definition helper text appears once", async () => {
  await renderSettings();
  await userEvent.click(screen.getByRole("button", { name: /^Builder/ }));
  expect(screen.getAllByText("Instructions given to the agent at the start of every session that uses this persona.")).toHaveLength(1);
});

it("changes apply note", async () => {
  await renderSettings("Agent memory");
  expect(screen.getAllByText("Changes apply to sessions created from now on.").length).toBeGreaterThan(0);
});

it("add persona rejects a name that collides with an existing persona or shared", async () => {
  await renderSettings();
  for (const name of ["builder", "Shared", "!!!"]) {
    await userEvent.click(screen.getByRole("button", { name: "Add persona" }));
    await userEvent.type(screen.getByLabelText("Name"), name);
    await userEvent.click(screen.getByRole("button", { name: "Save persona" }));
    expect(await screen.findByText(name === "!!!" ? "Give the persona a name." : "A persona with this name already exists.")).toBeInTheDocument();
  }
  expect(api.savePersona).not.toHaveBeenCalled();
});

it("a section has its own deep link", async () => {
  window.location.hash = "#/settings/memory";
  render(<Routed state={hubState()} onChanged={vi.fn()} />);
  expect(screen.getByRole("heading", { name: "Agent memory" })).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Personas" }));
  expect(window.location.hash).toBe("#/settings/personas");
});

it("personas can be renamed, except the Controller", async () => {
  await renderSettings();
  await userEvent.click(screen.getByRole("button", { name: /^Controller/ }));
  expect(screen.getByLabelText("Name")).toBeDisabled();
  await userEvent.click(screen.getByRole("button", { name: /^Builder/ }));
  const name = screen.getByLabelText("Name");
  expect(name).toBeEnabled();
  await userEvent.clear(name);
  await userEvent.type(name, "Smith");
  await userEvent.click(screen.getByRole("button", { name: "Save persona" }));
  expect(api.savePersona).toHaveBeenCalledWith(expect.objectContaining({ id: "builder", name: "Smith" }));
});

it("renaming a persona to another persona's name is rejected", async () => {
  await renderSettings();
  await userEvent.click(screen.getByRole("button", { name: /^Builder/ }));
  await userEvent.clear(screen.getByLabelText("Name"));
  await userEvent.type(screen.getByLabelText("Name"), "controller");
  await userEvent.click(screen.getByRole("button", { name: "Save persona" }));
  expect(await screen.findByText("A persona with this name already exists.")).toBeInTheDocument();
  expect(api.savePersona).not.toHaveBeenCalled();
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
