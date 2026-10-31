import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { hubState, question } from "../test/fixtures";
import InboxScreen from "./InboxScreen";

vi.mock("../api");

beforeEach(() => vi.resetAllMocks());

const renderInbox = (qs = [question({ options: ["Apple", "Banana"] })]) => {
  const onChanged = vi.fn();
  render(<InboxScreen state={hubState({ questions: qs })} onChanged={onChanged} />);
  return onChanged;
};

it("empty shows caught up", () => {
  render(<InboxScreen state={hubState()} onChanged={vi.fn()} />);
  expect(screen.getByText("You're all caught up")).toBeInTheDocument();
});

it("option click answers", async () => {
  vi.mocked(api.answerQuestion).mockResolvedValue(undefined);
  const onChanged = renderInbox();
  await userEvent.click(screen.getByRole("button", { name: "Banana" }));
  expect(api.answerQuestion).toHaveBeenCalledWith("s1:r1:0", { choice: "Banana" });
  expect(onChanged).toHaveBeenCalled();
});

it("free text reply", async () => {
  vi.mocked(api.answerQuestion).mockResolvedValue(undefined);
  renderInbox();
  await userEvent.type(screen.getByLabelText("Your reply"), "Something else");
  await userEvent.click(screen.getByRole("button", { name: "Send reply" }));
  expect(api.answerQuestion).toHaveBeenCalledWith("s1:r1:0", { text: "Something else" });
});

it("multi-select sends chosen labels joined", async () => {
  vi.mocked(api.answerQuestion).mockResolvedValue(undefined);
  renderInbox([question({ multiSelect: true, options: ["A", "B", "C"] })]);
  await userEvent.click(screen.getByLabelText("A"));
  await userEvent.click(screen.getByLabelText("C"));
  await userEvent.click(screen.getByRole("button", { name: "Send" }));
  expect(api.answerQuestion).toHaveBeenCalledWith("s1:r1:0", { text: "A, C" });
});

it("409 shows no-longer-waiting message and an Open in claude.ai link", async () => {
  vi.mocked(api.answerQuestion).mockRejectedValue(new Error("This question is no longer waiting for an answer."));
  renderInbox();
  await userEvent.click(screen.getByRole("button", { name: "Apple" }));
  expect(await screen.findByText("This question is no longer waiting for an answer.")).toBeInTheDocument();
  expect(screen.getByRole("link", { name: "Open in claude.ai" })).toHaveAttribute("href", "https://claude.ai/code/s1");
});

it("202 marks the question answered and shows how many remain", async () => {
  vi.mocked(api.answerQuestion).mockResolvedValue({ waitingFor: 1 });
  renderInbox([
    question({ id: "s1:r1:0", question: "First?", options: ["Yes", "No"] }),
    question({ id: "s1:r1:1", question: "Second?", options: ["Red", "Blue"] }),
  ]);
  expect(screen.getAllByRole("region")).toHaveLength(1);
  await userEvent.click(screen.getAllByRole("button", { name: "Yes" })[0]);
  expect(api.answerQuestion).toHaveBeenCalledWith("s1:r1:0", { choice: "Yes" });
  expect(await screen.findByText("Answer the remaining 1 question(s) to send.")).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Yes" })).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Red" })).toBeInTheDocument();
});
