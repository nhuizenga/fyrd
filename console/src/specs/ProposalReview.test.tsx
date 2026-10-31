import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { run } from "../test/fixtures";
import ProposalReview from "./ProposalReview";

vi.mock("../api");

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(api.getSpec).mockResolvedValue({ run: run(), markdown: "# Current spec", units: [] });
  vi.mocked(api.getProposal).mockResolvedValue({
    markdown: "# Proposed <b>spec</b>", summary: "Tightened scope", openQuestions: ["Which DB?"], sessionId: "s",
  });
});

async function open(onDone = vi.fn(), onClose = vi.fn()) {
  render(<ProposalReview specId="s1" onClose={onClose} onDone={onDone} />);
  await screen.findByText("Tightened scope");
  return { onDone, onClose };
}

it("shows both specs as plain text with summary and open questions", async () => {
  await open();
  expect(screen.getByText("# Current spec")).toBeInTheDocument();
  expect(screen.getByText("# Proposed <b>spec</b>")).toBeInTheDocument();
  expect(screen.getByText("Which DB?")).toBeInTheDocument();
});

it("Accept posts without markdown", async () => {
  vi.mocked(api.acceptProposal).mockResolvedValue(run());
  const { onDone } = await open();
  await userEvent.click(screen.getByRole("button", { name: "Accept" }));
  await waitFor(() => expect(api.acceptProposal).toHaveBeenCalledWith("s1", undefined));
  expect(onDone).toHaveBeenCalled();
});

it("Edit then accept sends the edited text", async () => {
  vi.mocked(api.acceptProposal).mockResolvedValue(run());
  await open();
  await userEvent.click(screen.getByRole("button", { name: "Edit then accept" }));
  const box = screen.getByRole("textbox", { name: "Edited proposal" });
  await userEvent.clear(box);
  await userEvent.type(box, "mine");
  await userEvent.click(screen.getByRole("button", { name: "Accept edited" }));
  await waitFor(() => expect(api.acceptProposal).toHaveBeenCalledWith("s1", "mine"));
});

it("Discard calls discard and shows a failure inline", async () => {
  vi.mocked(api.discardProposal).mockRejectedValueOnce(new Error("nope"));
  const { onDone } = await open();
  await userEvent.click(screen.getByRole("button", { name: "Discard" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("nope");
  expect(onDone).not.toHaveBeenCalled();
  vi.mocked(api.discardProposal).mockResolvedValue(run());
  await userEvent.click(screen.getByRole("button", { name: "Discard" }));
  await waitFor(() => expect(onDone).toHaveBeenCalled());
});
