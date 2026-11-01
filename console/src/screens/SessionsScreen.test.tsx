import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { FakeEventSource, item } from "../test/fakeEventSource";
import { hubState, question, session } from "../test/fixtures";
import { useRoute } from "../route";
import type { HubState } from "../types";
import type { ScreenProps } from "./ScreenProps";
import SessionsScreen from "./SessionsScreen";

vi.mock("../api");

beforeEach(() => {
  vi.resetAllMocks();
  window.location.hash = "";
  localStorage.clear();
});

/** SessionsScreen as the app mounts it: the selected session comes from the `#/sessions/{id}` route. */
function Routed(props: ScreenProps) {
  const { screen: current, id } = useRoute();
  return <SessionsScreen {...props} routeId={current === "sessions" ? id : null} />;
}

const renderScreen = (state: HubState) => {
  const onChanged = vi.fn();
  render(<Routed state={state} onChanged={onChanged} />);
  return onChanged;
};
const two = () =>
  hubState({
    sessions: [
      session({ id: "a", title: "alpha-1" }),
      session({ id: "c", title: "controller", role: "controller", tags: [] }),
      session({ id: "b", title: "beta-2", status: "idle" }),
    ],
    controller: { sessionId: "c", url: "https://claude.ai/code/c" },
  });

it("controller pinned first", () => {
  renderScreen(two());
  const rows = screen.getAllByRole("button", { name: /alpha-1|controller|beta-2/ }).filter((b) => b.className.includes("row-btn"));
  expect(rows[0]).toHaveTextContent("controller");
});

it("no controller shows the Adopt Controller panel", () => {
  renderScreen(hubState());
  expect(screen.getByText("Create a new session on claude.ai (no repo needed), then paste its session id here.")).toBeInTheDocument();
  const link = screen.getByRole("link", { name: "Open claude.ai/code" });
  expect(link).toHaveAttribute("href", "https://claude.ai/code");
  expect(link).toHaveAttribute("target", "_blank");
  expect(screen.queryByRole("button", { name: "Open Controller" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Start Controller" })).not.toBeInTheDocument();
});

it("Adopt sends the trimmed session id and refreshes", async () => {
  vi.mocked(api.adoptController).mockResolvedValue({ sessionId: "session_abc", url: "https://claude.ai/code/session_abc" });
  const onChanged = renderScreen(hubState());
  expect(screen.getByRole("button", { name: "Adopt" })).toBeDisabled();
  await userEvent.type(screen.getByLabelText("Session id"), "  session_abc ");
  await userEvent.click(screen.getByRole("button", { name: "Adopt" }));
  expect(api.adoptController).toHaveBeenCalledWith("session_abc");
  expect(onChanged).toHaveBeenCalled();
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
});

it("Adopt error shows in an alert", async () => {
  vi.mocked(api.adoptController).mockRejectedValue(
    new Error("That session isn't available. Create a new one on claude.ai and paste its id."));
  renderScreen(hubState());
  await userEvent.type(screen.getByLabelText("Session id"), "session_gone");
  await userEvent.click(screen.getByRole("button", { name: "Adopt" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("That session isn't available.");
});

it("Open Controller asks the server for a live Controller and opens that url", async () => {
  const open = vi.spyOn(window, "open").mockReturnValue(null);
  vi.mocked(api.openController).mockResolvedValue({ sessionId: "c2", url: "https://claude.ai/code/c2" });
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: "Open Controller" }));
  expect(api.openController).toHaveBeenCalled();
  expect(open).toHaveBeenCalledWith("https://claude.ai/code/c2", "_blank", "noopener");
  expect(open).not.toHaveBeenCalledWith("https://claude.ai/code/c", "_blank", "noopener");
});

it("Open Controller on 404 refreshes state instead of opening", async () => {
  const open = vi.spyOn(window, "open").mockReturnValue(null);
  vi.mocked(api.openController).mockRejectedValue(
    Object.assign(new Error("No Controller yet. Create a session on claude.ai and adopt it."), { status: 404 }));
  const onChanged = renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: "Open Controller" }));
  expect(onChanged).toHaveBeenCalled();
  expect(open).not.toHaveBeenCalled();
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
});

it("Replace Controller confirms, retires and refreshes", async () => {
  vi.mocked(api.retireController).mockResolvedValue(undefined);
  const onChanged = renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: "Replace Controller" }));
  expect(api.retireController).not.toHaveBeenCalled();
  expect(screen.getByText("This archives the current Controller. You'll then adopt a new one.")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Archive and replace" }));
  expect(api.retireController).toHaveBeenCalled();
  expect(onChanged).toHaveBeenCalled();
  expect(screen.queryByText("This archives the current Controller. You'll then adopt a new one.")).not.toBeInTheDocument();
});

it("Replace Controller can be cancelled", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: "Replace Controller" }));
  await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
  expect(api.retireController).not.toHaveBeenCalled();
});

it("select session streams its transcript", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  expect(FakeEventSource.latest().url).toBe("/api/hub/sessions/a/stream");
  FakeEventSource.latest().emit("item", item({ text: "Working on it" }));
  FakeEventSource.latest().emit("item", item({ role: "user", text: "Go ahead" }));
  expect(screen.getByText("Working on it").closest(".msg")).toHaveClass("msg-agent");
  expect(screen.getByText("Go ahead").closest(".msg")).toHaveClass("msg-user");
});

it("an ephemeral progress frame shows as an in-progress agent bubble until the next item", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  FakeEventSource.latest().emit("item", item({ text: "Starting" }));
  FakeEventSource.latest().emit("progress", item({ key: "ephemeral", seq: 0, kind: "progress", text: "Running the tests" }));
  const bubble = screen.getByText("Running the tests").closest(".msg");
  expect(bubble).toHaveClass("msg-agent");
  expect(bubble).toHaveClass("msg-progress");
  expect(within(bubble as HTMLElement).getByRole("status")).toBeInTheDocument();
  expect(screen.getByRole("log").lastElementChild?.lastElementChild).toBe(bubble);

  FakeEventSource.latest().emit("progress", item({ key: "ephemeral", seq: 0, kind: "progress", text: "Writing the fix" }));
  expect(screen.queryByText("Running the tests")).not.toBeInTheDocument();
  expect(screen.getByText("Writing the fix")).toBeInTheDocument();

  FakeEventSource.latest().emit("item", item({ text: "Done" }));
  expect(screen.queryByText("Writing the fix")).not.toBeInTheDocument();
});

it("switching sessions closes the old stream", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  const first = FakeEventSource.latest();
  await userEvent.click(screen.getByRole("button", { name: /beta-2/ }));
  expect(first.closed).toBe(true);
  expect(FakeEventSource.latest().url).toBe("/api/hub/sessions/b/stream");
});

it("duplicate items are shown once", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  const it1 = item({ text: "Only once" });
  FakeEventSource.latest().emit("item", it1);
  FakeEventSource.latest().emit("item", it1);
  expect(screen.getAllByText("Only once")).toHaveLength(1);
});

it("message text renders as markdown", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  FakeEventSource.latest().emit("item", item({ text: "Use **bold** and `code`" }));
  expect(screen.getByText("bold").tagName).toBe("STRONG");
  expect(screen.getByText("code").tagName).toBe("CODE");
});

it("a pending question shows answer buttons in the thread", async () => {
  vi.mocked(api.answerQuestion).mockResolvedValue(undefined);
  const st = two();
  st.sessions[0].status = "requires_action";
  st.questions = [question({ id: "q1", sessionId: "a", toolUseId: "tu1" })];
  render(<Routed state={st} onChanged={vi.fn()} />);
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  FakeEventSource.latest().emit("item", item({
    kind: "question", text: "Which DB?", toolUseId: "tu1",
    questions: [{ question: "Which DB?", multiSelect: false, options: [{ label: "Postgres" }, { label: "SQLite" }] }],
  }));
  const card = screen.getByRole("region", { name: "Question waiting for you" });
  expect(card).toHaveTextContent("Which DB?");
  await userEvent.click(screen.getByRole("button", { name: "Postgres" }));
  expect(api.answerQuestion).toHaveBeenCalledWith("q1", { choice: "Postgres" });
});

it("an answered question shows read-only, with the answer", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  FakeEventSource.latest().emit("item", item({
    kind: "question", text: "Which DB?", toolUseId: "tu1",
    questions: [{ question: "Which DB?", multiSelect: false, options: [{ label: "Postgres" }, { label: "SQLite" }] }],
  }));
  FakeEventSource.latest().emit("item", item({ role: "user", kind: "answer", text: "Which DB? → Postgres", toolUseId: "tu1" }));
  expect(screen.queryByRole("button", { name: "Postgres" })).not.toBeInTheDocument();
  expect(screen.getByText("Which DB? → Postgres").closest(".msg")).toHaveClass("msg-user");
});

it("a pending question the stream hasn't delivered still gets buttons", async () => {
  const st = two();
  st.questions = [question({ sessionId: "a" })];
  render(<Routed state={st} onChanged={vi.fn()} />);
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  expect(screen.getByRole("button", { name: "Postgres" })).toBeInTheDocument();
});

it("a stream fault shows a warning", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  FakeEventSource.latest().emit("fault", { detail: "Signed out of Claude" });
  expect(screen.getByRole("alert")).toHaveTextContent("Signed out of Claude");
});

it("Enter sends and Shift+Enter adds a new line", async () => {
  vi.mocked(api.sendMessage).mockResolvedValue(undefined);
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  const box = screen.getByLabelText("Message this session");
  await userEvent.type(box, "line one{Shift>}{Enter}{/Shift}line two");
  expect(api.sendMessage).not.toHaveBeenCalled();
  expect(box).toHaveValue("line one\nline two");
  await userEvent.type(box, "{Enter}");
  expect(api.sendMessage).toHaveBeenCalledWith("a", "line one\nline two");
  await vi.waitFor(() => expect(box).toHaveValue(""));
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

it("list order: Controller, its controls, search, then the other sessions", () => {
  renderScreen(two());
  const pane = screen.getByRole("complementary", { name: "Session list" });
  // Open/Replace are icon buttons on the Controller's own row.
  const ctlRow = screen.getByRole("button", { name: /controller/ }).closest("li")!;
  expect(pane).toContainElement(ctlRow);
  expect(ctlRow).toContainElement(screen.getByRole("button", { name: "Open Controller" }));
  expect(ctlRow).toContainElement(screen.getByRole("button", { name: "Replace Controller" }));
  const search = screen.getByLabelText("Search sessions");
  const open = screen.getByRole("button", { name: "Open Controller" });
  const alpha = screen.getByRole("button", { name: /alpha-1/ });
  expect(open.compareDocumentPosition(search) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  expect(search.compareDocumentPosition(alpha) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
});

it("search never hides the Controller", async () => {
  renderScreen(two());
  await userEvent.type(screen.getByLabelText("Search sessions"), "beta");
  expect(screen.getByRole("button", { name: /controller/ })).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: /alpha-1/ })).not.toBeInTheDocument();
});

it("status shows as a labelled icon", () => {
  renderScreen(two());
  const alpha = screen.getByRole("button", { name: /alpha-1/ });
  expect(alpha.querySelector("[role=img]")).toHaveAttribute("aria-label", "Running");
  expect(screen.getByRole("button", { name: /beta-2/ }).querySelector("[role=img]")).toHaveAttribute("aria-label", "Idle");
});

it("Add repository asks the session to attach the repo", async () => {
  vi.mocked(api.sendMessage).mockResolvedValue(undefined);
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  const add = screen.getByRole("button", { name: "Add repository" });
  expect(add).toBeDisabled();
  await userEvent.type(screen.getByLabelText("Add GitHub repository"), "https://github.com/acme/widgets/blob/main/README.md");
  await userEvent.click(add);
  const [id, text] = vi.mocked(api.sendMessage).mock.calls[0];
  expect(id).toBe("a");
  expect(text).toContain("owner `acme`, repo `widgets` and access `push`");
  expect(await screen.findByRole("status")).toHaveTextContent("Asked the session to attach acme/widgets.");
});

it("selecting a session sets a deep link, and a deep link selects the session", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /beta-2/ }));
  expect(window.location.hash).toBe("#/sessions/b");
  expect(screen.getByRole("heading", { name: "beta-2" })).toBeInTheDocument();
});

it("opening #/sessions/{id} directly shows that session", () => {
  window.location.hash = "#/sessions/a";
  renderScreen(two());
  expect(screen.getByRole("heading", { name: "alpha-1" })).toBeInTheDocument();
  expect(FakeEventSource.latest().url).toBe("/api/hub/sessions/a/stream");
});

it("details come first, in order, with relative times", async () => {
  const st = two();
  st.sessions[0] = { ...st.sessions[0], repo: "acme/web", createdAt: new Date(Date.now() - 86_400_000).toISOString(),
    lastActivity: new Date(Date.now() - 5 * 60_000).toISOString() };
  render(<Routed state={st} onChanged={vi.fn()} />);
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  const pane = screen.getByRole("complementary", { name: "Session details" });
  const headings = [...pane.querySelectorAll("h4")].map((h) => h.textContent);
  expect(headings).toEqual(["Details", "Estimated cost", "Tags", "Pull requests"]);
  expect([...pane.querySelectorAll("dt")].map((d) => d.textContent)).toEqual(["Role", "Repository", "Started", "Last activity"]);
  expect(pane).toHaveTextContent("Started1 day ago");
  expect(pane).toHaveTextContent("Last activity5 minutes ago");
  expect(pane).toHaveTextContent("Repositoryacme/web");
});

it("#/sessions reopens the most recently viewed session", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /beta-2/ }));
  cleanup();
  window.location.hash = "#/sessions";
  renderScreen(two());
  expect(await screen.findByRole("heading", { name: "beta-2" })).toBeInTheDocument();
  expect(window.location.hash).toBe("#/sessions/b");
});

it("a remembered session that no longer exists is ignored", () => {
  localStorage.setItem("fyrd.lastSession", "gone");
  window.location.hash = "#/sessions";
  renderScreen(two());
  expect(screen.getByText("Select a session to see its details.")).toBeInTheDocument();
  expect(window.location.hash).toBe("#/sessions");
});

it("hovering a message offers copy to clipboard", async () => {
  const writeText = vi.fn().mockResolvedValue(undefined);
  Object.assign(navigator, { clipboard: { writeText } });
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /alpha-1/ }));
  FakeEventSource.latest().emit("item", item({ text: "Copy **me**" }));
  await userEvent.click(screen.getByRole("button", { name: "Copy message" }));
  expect(writeText).toHaveBeenCalledWith("Copy **me**");
  expect(await screen.findByRole("button", { name: "Copied" })).toBeInTheDocument();
});
