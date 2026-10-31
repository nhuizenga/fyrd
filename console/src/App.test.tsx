import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "./api";
import App from "./App";
import { config, hubState, question } from "./test/fixtures";

vi.mock("./api");

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(api.getHubState).mockResolvedValue(hubState());
  vi.mocked(api.getConfig).mockResolvedValue(config());
});

it("renders three nav items with inbox count", async () => {
  vi.mocked(api.getHubState).mockResolvedValue(hubState({ questions: [question(), question({ id: "s1:r2:0" })] }));
  render(<App />);
  expect(await screen.findByRole("button", { name: "Inbox 2" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Sessions" })).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Settings" })).toBeInTheDocument();
});

it("switches screens from the nav", async () => {
  render(<App />);
  await userEvent.click(await screen.findByRole("button", { name: "Settings" }));
  expect(screen.getByRole("heading", { name: "Settings" })).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Sessions" }));
  expect(screen.getByRole("heading", { name: "Sessions" })).toBeInTheDocument();
});

it("shows signed-out banner", async () => {
  vi.mocked(api.getHubState).mockResolvedValue(hubState({ signedOut: true }));
  render(<App />);
  expect(await screen.findByRole("alert")).toHaveTextContent(/Signed out of Claude.*claude auth login/);
});

it("shows server error when state fails", async () => {
  vi.mocked(api.getHubState).mockRejectedValue(new Error("down"));
  render(<App />);
  expect(await screen.findByRole("alert")).toHaveTextContent("Can't reach the Fyrd server: down");
});
