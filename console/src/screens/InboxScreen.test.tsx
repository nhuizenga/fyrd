import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { FakeEventSource, item } from "../test/fakeEventSource";
import { hubState, question, session } from "../test/fixtures";
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

it("the answer box appears only after choosing Other", async () => {
  vi.mocked(api.answerQuestion).mockResolvedValue(undefined);
  renderInbox();
  expect(screen.queryByLabelText("Your answer")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Other" }));
  await userEvent.type(screen.getByLabelText("Your answer"), "Something else");
  await userEvent.click(screen.getByRole("button", { name: "Send answer" }));
  expect(api.answerQuestion).toHaveBeenCalledWith("s1:r1:0", { text: "Something else" });
});

it("multi-select Other adds free text to the chosen labels", async () => {
  vi.mocked(api.answerQuestion).mockResolvedValue(undefined);
  renderInbox([question({ multiSelect: true, options: ["A", "B"] })]);
  expect(screen.queryByLabelText("Your answer")).not.toBeInTheDocument();
  await userEvent.click(screen.getByLabelText("A"));
  await userEvent.click(screen.getByLabelText("Other"));
  await userEvent.type(screen.getByLabelText("Your answer"), "Z");
  await userEvent.click(screen.getByRole("button", { name: "Send" }));
  expect(api.answerQuestion).toHaveBeenCalledWith("s1:r1:0", { text: "A, Z" });
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

it("a budget 409 on answer shows the server's reason, with no claude.ai link, and can be retried", async () => {
  vi.mocked(api.answerQuestion).mockRejectedValue(new Error("This session reached its daily spend limit."));
  renderInbox();
  await userEvent.click(screen.getByRole("button", { name: "Apple" }));
  expect(await screen.findByText("This session reached its daily spend limit.")).toBeInTheDocument();
  expect(screen.queryByRole("link", { name: "Open in claude.ai" })).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Apple" })).toBeEnabled();
});

it("card shows repo and age, and the live conversation above the answer buttons", async () => {
  const askedAt = new Date(Date.now() - 5 * 60_000).toISOString();
  render(
    <InboxScreen
      state={hubState({
        sessions: [session({ id: "s1", repo: "acme/web" })],
        questions: [question({ askedAt, toolUseId: "tu1" })],
      })}
      onChanged={vi.fn()}
    />,
  );
  expect(screen.getByText(/acme\/web/)).toBeInTheDocument();
  expect(screen.getByText(/5 minutes ago/)).toBeInTheDocument();
  expect(FakeEventSource.latest().url).toBe("/api/hub/sessions/s1/stream");
  FakeEventSource.latest().emit("item", item({ text: "Which DB should I use?" }));
  FakeEventSource.latest().emit("item", item({ kind: "question", text: "Which DB?", toolUseId: "tu1",
    questions: [{ question: "Which DB?", multiSelect: false, options: [{ label: "Postgres" }] }] }));
  const log = screen.getByRole("log", { name: "Conversation" });
  expect(log).toHaveTextContent("Which DB should I use?");
  // The pending question shows once, as answer buttons, not again in the conversation.
  expect(log).not.toHaveTextContent("Postgres");
  const answer = screen.getByRole("button", { name: "Postgres" });
  expect(log.compareDocumentPosition(answer) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  const toggle = screen.getByRole("button", { name: "Show whole conversation" });
  expect(log).not.toHaveClass("inbox-log-expanded");
  await userEvent.click(toggle);
  expect(log).toHaveClass("inbox-log-expanded");
  expect(toggle).toHaveAttribute("aria-expanded", "true");
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
