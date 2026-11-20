import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { config, hubState } from "../test/fixtures";
import { useRoute } from "../route";
import type { ScreenProps } from "./ScreenProps";
import SettingsScreen from "./SettingsScreen";

vi.mock("../api");
// The persona table's chunk fails to load, as after a network blip or a rebuild that renamed the assets.
vi.mock("./PersonasCard", () => {
  throw new Error("Failed to fetch dynamically imported module");
});

function Routed(props: ScreenProps) {
  const { screen: current, id } = useRoute();
  return <SettingsScreen {...props} routeId={current === "settings" ? id : null} />;
}

beforeEach(() => {
  window.location.hash = "";
  vi.resetAllMocks();
  vi.mocked(api.getConfig).mockResolvedValue(config());
});

it("a persona table that fails to load shows an alert with Reload, and the rest of Settings stays", async () => {
  render(<Routed state={hubState()} onChanged={vi.fn()} />);
  await userEvent.click(screen.getByRole("button", { name: "Personas" }));
  const alert = await screen.findByRole("alert");
  expect(alert).toHaveTextContent("Couldn't load the persona table");
  expect(screen.getByRole("navigation", { name: "Settings sections" })).toBeInTheDocument();
  const reload = vi.fn();
  vi.spyOn(window, "location", "get").mockReturnValue({ ...window.location, reload });
  await userEvent.click(screen.getByRole("button", { name: "Reload" }));
  expect(reload).toHaveBeenCalledTimes(1);
});
