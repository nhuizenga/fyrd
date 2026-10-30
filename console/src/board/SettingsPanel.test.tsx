import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { config } from "../test/fixtures";
import SettingsPanel from "./SettingsPanel";

vi.mock("../api");

beforeEach(() => vi.resetAllMocks());

it("saves environment id, models and cap together", async () => {
  vi.mocked(api.putConfig).mockResolvedValue(config());
  const onSaved = vi.fn();
  render(<SettingsPanel config={config()} onSaved={onSaved} />);
  await userEvent.type(screen.getByLabelText("Environment ID"), "env_abc123");
  await userEvent.clear(screen.getByLabelText("Peer model"));
  await userEvent.type(screen.getByLabelText("Peer model"), "claude-haiku-5");
  await userEvent.click(screen.getByRole("button", { name: "Save" }));
  expect(api.putConfig).toHaveBeenCalledWith({
    squadCap: 3, environmentId: "env_abc123", leadModel: "claude-opus-5-5", peerModel: "claude-haiku-5",
  });
  await waitFor(() => expect(onSaved).toHaveBeenCalledWith(undefined));
});

it("shows the server's 400 detail", async () => {
  vi.mocked(api.putConfig).mockRejectedValue(new Error("EnvironmentId must match ^(env|ccpool)_[A-Za-z0-9]+$."));
  render(<SettingsPanel config={config()} onSaved={() => {}} />);
  await userEvent.type(screen.getByLabelText("Environment ID"), "nope");
  await userEvent.click(screen.getByRole("button", { name: "Save" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("EnvironmentId must match");
});

it("passes a relayError from the response to onSaved", async () => {
  vi.mocked(api.putConfig).mockResolvedValue({ ...config(), relayError: "lead unreachable" });
  const onSaved = vi.fn();
  render(<SettingsPanel config={config()} onSaved={onSaved} />);
  await userEvent.click(screen.getByRole("button", { name: "Save" }));
  await waitFor(() => expect(onSaved).toHaveBeenCalledWith("lead unreachable"));
});
