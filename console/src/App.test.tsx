import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "./api";
import App from "./App";
import { boardSpec, card, config, unit } from "./test/fixtures";
import type { Board } from "./types";

vi.mock("./api");

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(api.getInbox).mockResolvedValue([]);
  vi.mocked(api.getBoard).mockResolvedValue(board());
  vi.mocked(api.getConfig).mockResolvedValue(config());
  vi.mocked(api.getUndelivered).mockResolvedValue([]);
});

const board = (o: Partial<Board> = {}): Board => ({
  specs: [], signedOut: false, lastError: null, lastPollAt: null, rateLimit: null, ...o,
});

it("opens on the Inbox tab with the open-card count", async () => {
  vi.mocked(api.getInbox).mockResolvedValue([card({ question: "SSO provider?" })]);
  render(<App />);
  expect(await screen.findByRole("tab", { name: "Inbox (1)", selected: true })).toBeInTheDocument();
  expect(screen.getByText("SSO provider?")).toBeInTheDocument();
});

it("Board tab shows done count and spec groups", async () => {
  vi.mocked(api.getBoard).mockResolvedValue(board({
    specs: [boardSpec({
      units: [unit({ id: "a", stage: "Done", state: "Done" }), unit({ id: "b" })],
    })],
  }));
  render(<App />);
  const tab = await screen.findByRole("tab", { name: "Board · 1/2 done" });
  await userEvent.click(tab);
  expect(screen.getByText("Spec One")).toBeInTheDocument();
  expect(screen.getAllByText(/^[✓●○]{3}$/)).toHaveLength(2);
  expect(api.getSpec).not.toHaveBeenCalled();
});

it("one failing endpoint does not freeze the other data", async () => {
  vi.mocked(api.getInbox).mockRejectedValue(new Error("boom"));
  vi.mocked(api.getBoard).mockResolvedValue(board({ specs: [boardSpec({ units: [unit({ id: "a" })] })] }));
  render(<App />);
  await userEvent.click(await screen.findByRole("tab", { name: /^Board · 0\/1/ }));
  expect(screen.getByText("Spec One")).toBeInTheDocument();
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
});

it("shows the signed-out banner", async () => {
  vi.mocked(api.getBoard).mockResolvedValue(board({ signedOut: true }));
  render(<App />);
  expect(await screen.findByText(/Signed out of Claude/)).toHaveTextContent("run claude auth login");
});

it("shows lastError in an amber banner that can be dismissed", async () => {
  vi.mocked(api.getBoard).mockResolvedValue(board({ lastError: "poll <b>failed</b>" }));
  render(<App />);
  expect(await screen.findByText("poll <b>failed</b>")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Dismiss" }));
  expect(screen.queryByText("poll <b>failed</b>")).not.toBeInTheDocument();
});

it("shows a relay error from an inbox answer in the banner", async () => {
  vi.mocked(api.getInbox).mockResolvedValue([card({ options: ["A"] })]);
  vi.mocked(api.answer).mockResolvedValue({ ...card(), relayError: "lead unreachable" });
  render(<App />);
  await userEvent.click(await screen.findByRole("button", { name: "A" }));
  expect(await screen.findByText("lead unreachable")).toBeInTheDocument();
});

it("shows the rate-limit chip with status", async () => {
  vi.mocked(api.getBoard).mockResolvedValue(board({ rateLimit: { status: "allowed_warning", resetsAt: "2026-10-29T12:00:00Z" } }));
  render(<App />);
  expect(await screen.findByText(/Rate limit: allowed_warning/)).toBeInTheDocument();
});

it("shows the unreachable banner only when every endpoint fails", async () => {
  vi.mocked(api.getInbox).mockRejectedValue(new Error("down"));
  vi.mocked(api.getBoard).mockRejectedValue(new Error("down"));
  vi.mocked(api.getUndelivered).mockRejectedValue(new Error("down"));
  vi.mocked(api.getConfig).mockRejectedValue(new Error("down"));
  render(<App />);
  expect(await screen.findByRole("alert")).toHaveTextContent("Can't reach the Fyrd server");
});

it("Memory is the third tab and an undelivered answer shows in the Inbox", async () => {
  vi.mocked(api.getMemory).mockResolvedValue({ ownerProxy: "", global: "", repos: [] });
  vi.mocked(api.getUndelivered).mockResolvedValue([card({
    id: "f1", question: "Which DB?",
    answer: { text: "Postgres", remember: true, scope: "Repo", answeredAt: "x", relayStatus: "failed" },
  })]);
  render(<App />);
  expect(await screen.findByText("Answers not delivered")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("tab", { name: "Memory" }));
  expect(await screen.findByRole("textbox", { name: "Owner preferences" })).toBeInTheDocument();
});
