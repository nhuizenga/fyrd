import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { card, entry } from "../test/fixtures";
import InboxTab from "./InboxTab";

vi.mock("../api");

beforeEach(() => vi.resetAllMocks());

const failed = card({
  id: "f1", question: "Which DB?",
  answer: { text: "Postgres", remember: true, scope: "Repo", answeredAt: "x", relayStatus: "failed", relayAttempts: 5, relayError: "boom" },
});

it("shows the undelivered strip above open cards and retries", async () => {
  vi.mocked(api.relay).mockResolvedValue(failed);
  const onAnswered = vi.fn();
  render(<InboxTab cards={[card({ question: "Open one?" })]} undelivered={[failed]} specs={[entry()]} onAnswered={onAnswered} />);
  expect(screen.getByText("Answers not delivered")).toBeInTheDocument();
  expect(screen.getByText(/Postgres/)).toBeInTheDocument();
  expect(screen.getByText(/boom/)).toBeInTheDocument();
  expect(screen.getByText("Open one?")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Retry" }));
  await waitFor(() => expect(api.relay).toHaveBeenCalledWith("f1"));
  expect(onAnswered).toHaveBeenCalled();
});

it("shows a retry failure inline and hides the strip when empty", async () => {
  vi.mocked(api.relay).mockRejectedValue(new Error("Card not answered"));
  const { rerender } = render(<InboxTab cards={[]} undelivered={[failed]} specs={[]} onAnswered={() => {}} />);
  await userEvent.click(screen.getByRole("button", { name: "Retry" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Card not answered");
  rerender(<InboxTab cards={[]} undelivered={[]} specs={[]} onAnswered={() => {}} />);
  expect(screen.queryByText("Answers not delivered")).not.toBeInTheDocument();
  expect(screen.getByText("Nothing needs you right now.")).toBeInTheDocument();
});
