import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { hubState, question, session } from "../test/fixtures";
import type { HubState } from "../types";
import SessionsScreen from "./SessionsScreen";

vi.mock("../api");

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(api.getMessages).mockResolvedValue([
    { role: "agent", text: "Working on it", at: "t" },
    { role: "user", text: "Go ahead", at: "t" },
  ]);
});

const renderScreen = (state: HubState) => {
  const onChanged = vi.fn();
  render(<SessionsScreen state={state} onChanged={onChanged} />);
  return onChanged;
};
const two = () =>
  hubState({
    sessions: [
      session({ id: "a", title: "alpha-1" }),
      session({ id: "c", title: "controller", role: "controller", tags: ["fyrd-hub", "fyrd-controller"] }),
      session({ id: "b", title: "beta-2", status: "idle" }),
    ],
    controller: { sessionId: "c", url: "https://claude.ai/code/c" },
  });

it("controller pinned first", () => {
  renderScreen(two());
  const rows = screen.getAllByRole("button", { name: /alpha-1|controller|beta-2/ }).filter((b) => b.className.includes("row-btn"));
  expect(rows[0]).toHaveTextContent("controller");
});

it("no controller shows Start Controller", async () => {
  const open = vi.spyOn(window, "open").mockReturnValue(null);
  vi.mocked(api.openController).mockResolvedValue({ sessionId: "c", url: "https://claude.ai/code/c" });
  renderScreen(hubState());
  await userEvent.click(screen.getByRole("button", { name: "Start Controller" }));
  expect(api.openController).toHaveBeenCalled();
  expect(open).toHaveBeenCalledWith("https://claude.ai/code/c", "_blank", "noopener");
});

it("select session loads messages", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  expect(api.getMessages).toHaveBeenCalledWith("a");
  expect(await screen.findByText("Working on it")).toBeInTheDocument();
  expect(screen.getByText("Go ahead")).toBeInTheDocument();
});

it("Message this session sends", async () => {
  vi.mocked(api.sendMessage).mockResolvedValue(undefined);
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  await userEvent.type(screen.getByLabelText("Message this session"), "hello");
  await userEvent.click(screen.getByRole("button", { name: "Send" }));
  expect(api.sendMessage).toHaveBeenCalledWith("a", "hello");
});

it("pause and resume", async () => {
  vi.mocked(api.pause).mockResolvedValue(undefined);
  vi.mocked(api.resume).mockResolvedValue(undefined);
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  expect(screen.queryByRole("button", { name: "Resume session" })).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Pause session" }));
  expect(api.pause).toHaveBeenCalledWith("a");
  expect(await screen.findByRole("button", { name: "Resume session" })).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Pause session" })).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: /beta-2/ }));
  await userEvent.click(screen.getByRole("button", { name: "Resume session" }));
  expect(api.resume).toHaveBeenCalledWith("b");
});

it("cost and budget are placeholders", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  expect(screen.getByRole("button", { name: "+$5" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "+$100" })).toBeDisabled();
  expect(screen.getByText("Estimated cost").nextElementSibling).toHaveTextContent("—");
});

it("search filters by name", async () => {
  renderScreen(two());
  await userEvent.type(screen.getByLabelText("Search sessions"), "beta");
  expect(screen.getByText("beta-2")).toBeInTheDocument();
  expect(screen.queryByText("alpha-1")).not.toBeInTheDocument();
  await userEvent.clear(screen.getByLabelText("Search sessions"));
  await userEvent.type(screen.getByLabelText("Search sessions"), "zzz");
  expect(screen.getByText("No sessions match that search.")).toBeInTheDocument();
});

it("Respond in inbox navigates to the inbox", async () => {
  const onOpenInbox = vi.fn();
  const st = two();
  st.sessions[0].status = "requires_action";
  st.questions = [question({ sessionId: "a" })];
  render(<SessionsScreen state={st} onChanged={vi.fn()} onOpenInbox={onOpenInbox} />);
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  await userEvent.click(screen.getByRole("button", { name: "Respond in inbox" }));
  expect(onOpenInbox).toHaveBeenCalled();
});

it("refreshes messages when the poll timestamp changes", async () => {
  const st = two();
  const { rerender } = render(<SessionsScreen state={st} onChanged={vi.fn()} />);
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  await screen.findByText("Working on it");
  expect(api.getMessages).toHaveBeenCalledTimes(1);
  rerender(<SessionsScreen state={{ ...st, lastPollAt: "2026-10-31T01:00:00Z" }} onChanged={vi.fn()} />);
  await vi.waitFor(() => expect(api.getMessages).toHaveBeenCalledTimes(2));
});
