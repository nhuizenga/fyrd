import { act, cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, vi } from "vitest";
import * as api from "../api";
import { FakeEventSource, item } from "../test/fakeEventSource";
import { cost, hubState, question, session } from "../test/fixtures";
import { useRoute } from "../route";
import type { HubState, PullRequestStatus } from "../types";
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

it("session rows show the server-kept progress of sessions that are not selected", () => {
  const live = (o: object) => ({ key: "ephemeral", seq: 0, role: "agent" as const, kind: "progress" as const, text: "", at: "2026-11-01T00:00:00Z", ...o });
  renderScreen({
    ...two(),
    progress: {
      a: live({ status: "Using Bash" }),
      b: live({ text: "First line\nWriting the report" }),
    },
  });
  // Nothing is selected, so these come from the hub state rather than a transcript stream.
  expect(FakeEventSource.instances).toHaveLength(0);
  expect(screen.getByText("Using Bash")).toHaveClass("session-row-progress");
  expect(screen.getByText("Writing the report")).toHaveClass("session-row-progress");
  expect(screen.queryByText("First line")).not.toBeInTheDocument();
});

it("session rows show no progress line when the server has none", () => {
  renderScreen(two());
  expect(document.querySelector(".session-row-progress")).toBeNull();
});

it("select session streams its transcript", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  expect(FakeEventSource.latest().url).toBe("/api/hub/sessions/a/stream");
  FakeEventSource.latest().emit("item", item({ text: "Working on it" }));
  FakeEventSource.latest().emit("item", item({ role: "user", text: "Go ahead" }));
  expect(screen.getByText("Working on it").closest(".msg")).toHaveClass("msg-agent");
  expect(screen.getByText("Go ahead").closest(".msg")).toHaveClass("msg-user");
});

it("an ephemeral progress frame shows as an in-progress agent bubble until the next item", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  FakeEventSource.latest().emit("item", item({ text: "Starting" }));
  FakeEventSource.latest().emit("progress", item({ key: "ephemeral", seq: 0, kind: "progress", text: "", status: "Running the tests" }));
  const bubble = screen.getByText("Running the tests").closest(".msg");
  expect(bubble).toHaveClass("msg-agent");
  expect(bubble).toHaveClass("msg-progress");
  expect(within(bubble as HTMLElement).getByRole("status")).toBeInTheDocument();
  expect(screen.getByRole("log").lastElementChild?.lastElementChild).toBe(bubble);

  FakeEventSource.latest().emit("progress", item({ key: "ephemeral", seq: 0, kind: "progress", text: "", status: "Writing the fix" }));
  expect(screen.queryByText("Running the tests")).not.toBeInTheDocument();
  expect(screen.getByText("Writing the fix")).toBeInTheDocument();

  FakeEventSource.latest().emit("item", item({ text: "Done" }));
  expect(screen.queryByText("Writing the fix")).not.toBeInTheDocument();
});

it("switching sessions closes the old stream", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  const first = FakeEventSource.latest();
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*beta-2/ }));
  expect(first.closed).toBe(true);
  expect(FakeEventSource.latest().url).toBe("/api/hub/sessions/b/stream");
});

it("duplicate items are shown once", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  const it1 = item({ text: "Only once" });
  FakeEventSource.latest().emit("item", it1);
  FakeEventSource.latest().emit("item", it1);
  expect(screen.getAllByText("Only once")).toHaveLength(1);
});

it("message text renders as markdown", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
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
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
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
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
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
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  expect(screen.getByRole("button", { name: "Postgres" })).toBeInTheDocument();
});

it("a stream fault shows a warning", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  FakeEventSource.latest().emit("fault", { detail: "Signed out of Claude" });
  expect(screen.getByRole("alert")).toHaveTextContent("Signed out of Claude");
});

it("Enter sends and Shift+Enter adds a new line", async () => {
  vi.mocked(api.sendMessage).mockResolvedValue(undefined);
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
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
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  await userEvent.type(screen.getByLabelText("Message this session"), "hello");
  await userEvent.click(screen.getByRole("button", { name: "Send" }));
  expect(api.sendMessage).toHaveBeenCalledWith("a", "hello");
});

it("pause and resume", async () => {
  vi.mocked(api.pause).mockResolvedValue(undefined);
  vi.mocked(api.resume).mockResolvedValue(undefined);
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  expect(screen.queryByRole("button", { name: "Resume session" })).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Pause session" }));
  expect(api.pause).toHaveBeenCalledWith("a");
  expect(await screen.findByRole("button", { name: "Resume session" })).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Pause session" })).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*beta-2/ }));
  await userEvent.click(screen.getByRole("button", { name: "Resume session" }));
  expect(api.resume).toHaveBeenCalledWith("b");
});

const withCost = (c: Parameters<typeof cost>[0], controller = false) => {
  const st = two();
  st.sessions[controller ? 1 : 0] = { ...st.sessions[controller ? 1 : 0], cost: cost(c) };
  return st;
};
// The sidebar row, not its trash button (whose name is "Archive …").
const open = (name: RegExp) =>
  userEvent.click(screen.getAllByRole("button", { name }).find((b) => !/^Archive/.test(b.getAttribute("aria-label") ?? b.textContent ?? ""))!);

it("no cost data shows a dash and disabled raises", async () => {
  renderScreen(two());
  await open(/alpha-1/);
  expect(screen.queryByRole("button", { name: /\+\$5 / })).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: /\+\$25/ })).toBeDisabled();
  expect(screen.getByRole("button", { name: /\+\$100/ })).toBeDisabled();
  expect(screen.getByText("Estimated cost").nextElementSibling).toHaveTextContent("—");
});

it("cost block shows amount, bar width and label from the ratio", async () => {
  renderScreen(withCost({ estimated: true }));
  await open(/alpha-1/);
  expect(screen.getByText("$31.00 (estimated)")).toBeInTheDocument();
  const bar = screen.getByRole("progressbar", { name: "62 percent of today's $50.00 limit used" });
  expect(bar).toHaveValue(62);
  expect(screen.getByText("62% of $50.00 limit today")).toBeInTheDocument();
});

it("bar width is capped at 100%", async () => {
  renderScreen(withCost({ costToday: 80, ratio: 1.6 }));
  await open(/alpha-1/);
  expect(screen.getByRole("progressbar", { name: /160 percent/ })).toHaveValue(100);
});

it("only +$25 and +$100 render, enabled when canExtend, and post the amount", async () => {
  vi.mocked(api.addBudget).mockResolvedValue(cost({ canExtend: true }));
  const { rerender } = render(<Routed state={withCost({ ratio: 0.89 })} onChanged={vi.fn()} />);
  await open(/alpha-1/);
  expect(screen.getByRole("button", { name: "+$25 to today's limit" })).toBeDisabled();
  expect(screen.getByText("Raises unlock at 90% of today's limit.")).toBeInTheDocument();
  const onChanged = vi.fn();
  rerender(<Routed state={withCost({ ratio: 0.9, canExtend: true })} onChanged={onChanged} />);
  expect(screen.queryByText(/Raises unlock/)).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: /\+\$5 / })).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "+$100 to today's limit" }));
  expect(api.addBudget).toHaveBeenCalledWith("a", 100);
  await userEvent.click(await screen.findByRole("button", { name: "+$25 to today's limit" }));
  expect(api.addBudget).toHaveBeenCalledWith("a", 25);
  expect(onChanged).toHaveBeenCalled();
});

it("raise buttons are disabled while a raise is in flight, and the returned view shows until the poll catches up", async () => {
  let finish!: (v: ReturnType<typeof cost>) => void;
  vi.mocked(api.addBudget).mockReturnValue(new Promise((r) => { finish = r; }));
  const stale = withCost({ ratio: 0.95, canExtend: true, costToday: 47.5 });
  render(<Routed state={stale} onChanged={vi.fn()} />);
  await open(/alpha-1/);
  await userEvent.click(screen.getByRole("button", { name: "+$25 to today's limit" }));
  expect(screen.getByRole("button", { name: "+$25 to today's limit" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "+$100 to today's limit" })).toBeDisabled();
  await userEvent.click(screen.getByRole("button", { name: "+$100 to today's limit" }));
  expect(api.addBudget).toHaveBeenCalledTimes(1);
  finish(cost({ costToday: 47.5, limitUsd: 75, ratio: 0.63, canExtend: false }));
  // The poll still holds the old view, but the returned one wins until the server limit changes.
  expect(await screen.findByText("63% of $75.00 limit today")).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "+$25 to today's limit" })).toBeDisabled();
});

it("raises stay disabled while the fyrd is over its limit, even if a view says canExtend", async () => {
  renderScreen(withCost({ blocked: "fyrd", ratio: 0.95, canExtend: true }));
  await open(/alpha-1/);
  expect(screen.getByRole("button", { name: "+$25 to today's limit" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "+$100 to today's limit" })).toBeDisabled();
});

it("session block disables Send and Resume with an Alert", async () => {
  const st = withCost({ blocked: "session", ratio: 1, costToday: 50 });
  st.sessions[0] = { ...st.sessions[0], status: "idle" };
  renderScreen(st);
  await open(/alpha-1/);
  expect(screen.getByRole("status")).toHaveTextContent("Add budget to continue");
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Send" })).toHaveAttribute("aria-describedby", "budget-block");
  expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
  await userEvent.type(screen.getByLabelText("Message this session"), "hi");
  expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Resume session" })).toBeDisabled();
});

it("fyrd block shows the paused Alert; the Controller stays sendable", async () => {
  const st = withCost({ blocked: "fyrd" });
  st.sessions[1] = { ...st.sessions[1], cost: cost({ exempt: true, blocked: null }) };
  st.budget = { date: "2026-11-01", timeZone: "UTC", fyrdCostToday: 500, fyrdLimitUsd: 500, fyrdBlocked: true };
  renderScreen(st);
  await open(/alpha-1/);
  expect(screen.getByRole("status")).toHaveTextContent("Sending is paused until midnight except for the Controller");
  await userEvent.click(screen.getAllByRole("button", { name: /controller/ }).find((b) => b.className.includes("row-btn"))!);
  expect(screen.queryByText(/Sending is paused/)).not.toBeInTheDocument();
  await userEvent.type(screen.getByLabelText("Message this session"), "hi");
  expect(screen.getByRole("button", { name: "Send" })).toBeEnabled();
});

it("Controller row says the limit is not enforced", async () => {
  renderScreen(withCost({ exempt: true }, true));
  await userEvent.click(screen.getAllByRole("button", { name: /controller/ }).find((b) => b.className.includes("row-btn"))!);
  expect(screen.getByText("Controller · limit not enforced")).toBeInTheDocument();
});

it("header shows the fyrd total against its limit", () => {
  const st = two();
  st.budget = { date: "2026-11-01", timeZone: "UTC", fyrdCostToday: 123.4, fyrdLimitUsd: 500, fyrdBlocked: false };
  renderScreen(st);
  expect(screen.getByText("Fyrd today: $123.40 of $500.00")).toBeInTheDocument();
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
  const ctlRow = screen.getByRole("button", { name: /^(?!Export).*controller/ }).closest("li")!;
  expect(pane).toContainElement(ctlRow);
  expect(ctlRow).toContainElement(screen.getByRole("button", { name: "Open Controller" }));
  expect(ctlRow).toContainElement(screen.getByRole("button", { name: "Replace Controller" }));
  const search = screen.getByLabelText("Search sessions");
  const open = screen.getByRole("button", { name: "Open Controller" });
  const alpha = screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ });
  expect(open.compareDocumentPosition(search) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  expect(search.compareDocumentPosition(alpha) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
});

it("search never hides the Controller", async () => {
  renderScreen(two());
  await userEvent.type(screen.getByLabelText("Search sessions"), "beta");
  expect(screen.getByRole("button", { name: /^(?!Export).*controller/ })).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ })).not.toBeInTheDocument();
});

it("status shows as a labelled icon", () => {
  renderScreen(two());
  const alpha = screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ });
  expect(alpha.querySelector("[role=img]")).toHaveAttribute("aria-label", "Running");
  expect(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*beta-2/ }).querySelector("[role=img]")).toHaveAttribute("aria-label", "Idle");
});

it("Add repository asks the session to attach the repo", async () => {
  vi.mocked(api.sendMessage).mockResolvedValue(undefined);
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
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
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*beta-2/ }));
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
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  const pane = screen.getByRole("complementary", { name: "Session details" });
  const headings = [...pane.querySelectorAll("h4")].map((h) => h.textContent);
  expect(headings).toEqual(["Details", "Estimated cost", "Tags", "Pull requests"]);
  expect([...pane.querySelectorAll("dt")].map((d) => d.textContent)).toEqual(["Role", "Repository", "Branch", "Started", "Last activity"]);
  expect(pane).toHaveTextContent("Started1 day ago");
  expect(pane).toHaveTextContent("Last activity5 minutes ago");
  expect(pane).toHaveTextContent("Repositoryacme/web");
});

it("#/sessions reopens the most recently viewed session", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*beta-2/ }));
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
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  FakeEventSource.latest().emit("item", item({ text: "Copy **me**" }));
  await userEvent.click(screen.getByRole("button", { name: "Copy message" }));
  expect(writeText).toHaveBeenCalledWith("Copy **me**");
  expect(await screen.findByRole("button", { name: "Copied" })).toBeInTheDocument();
});

const PR12 = "https://github.com/acme/widgets/pull/12";
const withRepo = () =>
  hubState({
    sessions: [
      session({ id: "a", title: "alpha-1", repo: "acme/widgets", branch: "fyrd/alpha", pullRequests: [PR12] }),
      session({ id: "b", title: "beta-2" }),
    ],
  });

it("an ephemeral progress bubble is still there after switching sessions and coming back", async () => {
  renderScreen(withRepo());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  FakeEventSource.latest().emit("progress", item({ key: "ephemeral", seq: 0, kind: "progress", text: "", status: "Running the tests" }));
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*beta-2/ }));
  expect(screen.queryByText("Running the tests")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  expect(screen.getByText("Running the tests")).toBeInTheDocument();
});

const PR40 = "https://github.com/acme/widgets/pull/40";
const withTwoPrs = () =>
  hubState({ sessions: [session({ id: "a", title: "alpha-1", repo: "acme/widgets", pullRequests: [PR12, PR40] })] });

it("a session row with several PRs shows one 'n PRs' chip instead of each number", () => {
  renderScreen(withTwoPrs());
  const row = screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ });
  expect(within(row).getByText("2 PRs")).toBeInTheDocument();
  expect(within(row).getByTitle("#12, #40")).toBeInTheDocument();
  expect(within(row).queryByText("#12")).not.toBeInTheDocument();
});

it("session details have no mentioned-PRs section", async () => {
  renderScreen(withRepo());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  const details = screen.getByRole("complementary", { name: "Session details" });
  expect(within(details).queryByRole("heading", { name: /Mentioned/ })).not.toBeInTheDocument();
});

it("session rows show a repo chip and PR chips", () => {
  renderScreen(withRepo());
  const row = screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ });
  expect(within(row).getByText("widgets")).toBeInTheDocument();
  expect(within(row).getByText("#12")).toBeInTheDocument();
  const bare = screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*beta-2/ });
  expect(within(bare).queryByText("widgets")).not.toBeInTheDocument();
});

it("session details show each PR as a chip with an Open button", async () => {
  const open = vi.spyOn(window, "open").mockReturnValue(null);
  renderScreen(withRepo());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  const details = screen.getByRole("complementary", { name: "Session details" });
  expect(within(details).getByText("#12")).toBeInTheDocument();
  expect(within(details).queryByRole("link", { name: "#12" })).not.toBeInTheDocument();
  const openBtn = within(details).getByRole("button", { name: "Open #12" });
  expect(openBtn.textContent).toBe("");
  await userEvent.click(openBtn);
  expect(open).toHaveBeenCalledWith(PR12, "_blank", "noopener,noreferrer");
  open.mockRestore();
});

it("the message draft belongs to each session and survives switching and remounting", async () => {
  renderScreen(withRepo());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  await userEvent.type(screen.getByLabelText("Message this session"), "for alpha");
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*beta-2/ }));
  expect(screen.getByLabelText("Message this session")).toHaveValue("");
  await userEvent.type(screen.getByLabelText("Message this session"), "for beta");
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  expect(screen.getByLabelText("Message this session")).toHaveValue("for alpha");

  cleanup();
  renderScreen(withRepo());
  expect(screen.getByLabelText("Message this session")).toHaveValue("for alpha");
});

it("a sent message clears only that session's saved draft", async () => {
  vi.mocked(api.sendMessage).mockResolvedValue(undefined);
  renderScreen(withRepo());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  await userEvent.type(screen.getByLabelText("Message this session"), "go{Enter}");
  await vi.waitFor(() => expect(screen.getByLabelText("Message this session")).toHaveValue(""));
  cleanup();
  renderScreen(withRepo());
  expect(screen.getByLabelText("Message this session")).toHaveValue("");
});

it("a sliding divider resizes the message box", async () => {
  renderScreen(withRepo());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  const divider = screen.getByRole("separator", { name: "Resize message box" });
  const before = Number(divider.getAttribute("aria-valuenow"));
  divider.focus();
  await userEvent.keyboard("{ArrowUp}");
  expect(Number(divider.getAttribute("aria-valuenow"))).toBeGreaterThan(before);
  await userEvent.keyboard("{ArrowDown}{ArrowDown}");
  expect(Number(divider.getAttribute("aria-valuenow"))).toBeLessThan(before);
});

it("the key hints are icons and Send sits below the message box", async () => {
  renderScreen(withRepo());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  expect(screen.queryByText(/Enter to send/)).not.toBeInTheDocument();
  expect(screen.getByRole("img", { name: "Enter to send" })).toBeInTheDocument();
  expect(screen.getByRole("img", { name: "Shift+Enter for a new line" })).toBeInTheDocument();
  expect(screen.getByRole("img", { name: "Enter to send" })).toHaveTextContent("Send");
  expect(screen.getByRole("img", { name: "Shift+Enter for a new line" })).toHaveTextContent("New line");
  const box = screen.getByLabelText("Message this session");
  const send = screen.getByRole("button", { name: "Send" });
  expect(box.compareDocumentPosition(send) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  expect(send.closest(".composer-actions")).not.toBeNull();
  expect(send.closest(".composer-actions")?.contains(box)).toBe(false);
});

it("each non-Controller session row has a trash button that confirms, then archives that session", async () => {
  vi.mocked(api.archiveSession).mockResolvedValue(undefined);
  const onChanged = renderScreen(two());
  expect(screen.getAllByRole("button", { name: /^Archive / })).toHaveLength(2);
  expect(screen.queryByRole("button", { name: "Archive controller" })).not.toBeInTheDocument();

  await userEvent.click(screen.getByRole("button", { name: "Archive beta-2" }));
  expect(api.archiveSession).not.toHaveBeenCalled();
  expect(screen.getByText("Archive beta-2?")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Archive session" }));
  expect(api.archiveSession).toHaveBeenCalledWith("b");
  expect(onChanged).toHaveBeenCalled();
});

it("each non-Controller row has a pencil button, left of the trash, that renames the session", async () => {
  vi.mocked(api.renameSession).mockResolvedValue(undefined);
  const onChanged = renderScreen(two());
  expect(screen.getAllByRole("button", { name: /^Rename / })).toHaveLength(2);
  expect(screen.queryByRole("button", { name: "Rename controller" })).not.toBeInTheDocument();
  const pencil = screen.getByRole("button", { name: "Rename beta-2" });
  const trash = screen.getByRole("button", { name: "Archive beta-2" });
  expect(pencil.compareDocumentPosition(trash) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

  await userEvent.click(pencil);
  const box = screen.getByLabelText("Session name");
  expect(box).toHaveValue("beta-2");
  await userEvent.clear(box);
  await userEvent.type(box, "gamma-3");
  await userEvent.click(screen.getByRole("button", { name: "Save name" }));
  expect(api.renameSession).toHaveBeenCalledWith("b", "gamma-3");
  expect(onChanged).toHaveBeenCalled();
  expect(screen.queryByLabelText("Session name")).not.toBeInTheDocument();
});

it("renaming can be cancelled and a blank or unchanged name is not sent", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: "Rename beta-2" }));
  await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
  expect(screen.queryByLabelText("Session name")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Rename beta-2" }));
  await userEvent.click(screen.getByRole("button", { name: "Save name" }));
  expect(api.renameSession).not.toHaveBeenCalled();
});

it("sessions are listed by role, then by name", () => {
  const mk = (id: string, title: string, role: string) => ({ ...two().sessions[0], id, title, role });
  const base = two();
  renderScreen({ ...base, sessions: [
    base.sessions.find((s) => s.id !== "a" && s.id !== "b")!,
    mk("1", "reviewer-a", "reviewer"), mk("2", "builder-10", "builder"), mk("3", "builder-2", "builder"), mk("4", "Alpha", "validator"),
  ].filter(Boolean) });
  const titles = screen.getAllByRole("button", { name: /^Archive / }).map((b) => b.getAttribute("aria-label"));
  expect(titles).toEqual(["Archive builder-2", "Archive builder-10", "Archive reviewer-a", "Archive Alpha"]);
});

it("a progress bubble shows the running tool's command", async () => {
  renderScreen(withRepo());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  FakeEventSource.latest().emit("progress", item({ key: "ephemeral", seq: 0, kind: "progress", text: "", status: "Using Bash", detail: "git status && ls" }));
  expect(screen.getByText("git status && ls")).toBeInTheDocument();
});

it("the list and details panes resize with the keyboard, and the width is remembered", async () => {
  const { unmount } = render(<Routed state={two()} onChanged={vi.fn()} />);
  const list = screen.getByRole("separator", { name: "Resize session list" });
  const details = screen.getByRole("separator", { name: "Resize session details" });
  expect(list).toHaveAttribute("aria-valuenow", "280");
  list.focus();
  await userEvent.keyboard("{ArrowRight}");
  expect(list).toHaveAttribute("aria-valuenow", "304");
  details.focus();
  await userEvent.keyboard("{ArrowRight}"); // dragging the details divider right shrinks that pane
  expect(details).toHaveAttribute("aria-valuenow", "256");
  expect(document.querySelector<HTMLElement>(".sessions-grid")!.style.getPropertyValue("--list-w")).toBe("304px");
  unmount();
  renderScreen(two());
  expect(screen.getByRole("separator", { name: "Resize session list" })).toHaveAttribute("aria-valuenow", "304");
  localStorage.clear();
});

it("a failed rename keeps the form and the typed name", async () => {
  vi.mocked(api.renameSession).mockRejectedValue(new Error("boom"));
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: "Rename beta-2" }));
  const box = screen.getByLabelText("Session name");
  await userEvent.clear(box);
  await userEvent.type(box, "gamma-3");
  await userEvent.click(screen.getByRole("button", { name: "Save name" }));
  expect(await screen.findByText("boom")).toBeInTheDocument();
  expect(screen.getByLabelText("Session name")).toHaveValue("gamma-3");
});

it("archiving can be cancelled", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: "Archive beta-2" }));
  await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
  expect(api.archiveSession).not.toHaveBeenCalled();
  expect(screen.queryByText("Archive beta-2?")).not.toBeInTheDocument();
});

it("session rows no longer show a role line", () => {
  renderScreen(two());
  const row = screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ });
  expect(within(row).queryByText("builder")).not.toBeInTheDocument();
  const ctl = screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*controller/ });
  expect(within(ctl).queryByText("Controller")).not.toBeInTheDocument();
});

const prStatus = (o: Partial<PullRequestStatus> = {}): PullRequestStatus => ({
  url: PR12, number: 12, title: "Fix it", state: "open", draft: false, checks: "passing", canMerge: true,
  base: "main", head: "fyrd/alpha", checksPassed: 0, checksFailed: 0, checksPending: 0, failingCheck: null,
  review: "none", mergeable: "mergeable", mergeState: "clean", ...o,
});
const openAlphaWith = async (pr: PullRequestStatus) => {
  vi.mocked(api.pullRequests).mockResolvedValue([pr]);
  const onChanged = renderScreen(withRepo());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  const details = screen.getByRole("complementary", { name: "Session details" });
  await within(details).findByRole("button", { name: "Open #12" });
  return { details, onChanged };
};

it("a draft PR gets a green check button that marks it ready, and no merge button", async () => {
  vi.mocked(api.markPrReady).mockResolvedValue(undefined);
  const { details, onChanged } = await openAlphaWith(prStatus({ draft: true, canMerge: false }));
  expect(within(details).getByText("Draft")).toBeInTheDocument();
  expect(within(details).queryByRole("button", { name: "Merge #12" })).not.toBeInTheDocument();
  const ready = within(details).getByRole("button", { name: "Mark #12 ready for review" });
  expect(ready.textContent).toBe("");
  expect(ready).toHaveClass("pr-ready");
  await userEvent.click(ready);
  expect(api.markPrReady).toHaveBeenCalledWith("a", PR12);
  expect(api.pullRequests).toHaveBeenCalledTimes(2);
  expect(onChanged).toHaveBeenCalled();
});

it("a ready PR with passing checks gets a merge button that confirms first", async () => {
  vi.mocked(api.mergePr).mockResolvedValue(undefined);
  const { details } = await openAlphaWith(prStatus());
  expect(within(details).queryByRole("button", { name: "Mark #12 ready for review" })).not.toBeInTheDocument();
  const merge = within(details).getByRole("button", { name: "Merge #12" });
  expect(merge.textContent).toBe("");
  expect(merge).toHaveClass("pr-merge");
  await userEvent.click(merge);
  expect(api.mergePr).not.toHaveBeenCalled();
  expect(within(details).getByText("Merge #12 Fix it?")).toBeInTheDocument();
  await userEvent.click(within(details).getByRole("button", { name: "Merge pull request" }));
  expect(api.mergePr).toHaveBeenCalledWith("a", PR12);
});

it("merging can be cancelled", async () => {
  const { details } = await openAlphaWith(prStatus());
  await userEvent.click(within(details).getByRole("button", { name: "Merge #12" }));
  await userEvent.click(within(details).getByRole("button", { name: "Cancel" }));
  expect(api.mergePr).not.toHaveBeenCalled();
});

it.each([
  ["checks failing", { checks: "failing", canMerge: false }],
  ["checks pending", { checks: "pending", canMerge: false }],
  ["already merged", { state: "merged", canMerge: false }],
] as const)("no ready or merge button when %s", async (_n, o) => {
  const { details } = await openAlphaWith(prStatus(o));
  expect(within(details).queryByRole("button", { name: "Merge #12" })).not.toBeInTheDocument();
  expect(within(details).queryByRole("button", { name: "Mark #12 ready for review" })).not.toBeInTheDocument();
});

it("a PR action failure shows in an alert", async () => {
  vi.mocked(api.markPrReady).mockRejectedValue(new Error("gh failed"));
  const { details } = await openAlphaWith(prStatus({ draft: true, canMerge: false }));
  await userEvent.click(within(details).getByRole("button", { name: "Mark #12 ready for review" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("gh failed");
});

const progress = (o: { text?: string; status?: string | null }) =>
  item({ key: "ephemeral", seq: 0, kind: "progress", text: "", status: null, ...o });

it("the progress bubble shows the streamed reply as markdown under the status line", async () => {
  renderScreen(withRepo());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  FakeEventSource.latest().emit("progress", progress({ status: "Using Read", text: "Looking at **the parser**" }));
  const bubble = screen.getByText("Using Read").closest(".msg") as HTMLElement;
  expect(bubble).toHaveClass("msg-progress");
  expect(within(bubble).getByRole("status")).toBeInTheDocument();
  expect(within(bubble).getByText("the parser").tagName).toBe("STRONG");
});

it("text with no status falls back to a generic working label", async () => {
  renderScreen(withRepo());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  FakeEventSource.latest().emit("progress", progress({ text: "Partial reply" }));
  expect(screen.getByText("Working…")).toBeInTheDocument();
  expect(screen.getByText("Partial reply")).toBeInTheDocument();
});

it("an empty progress frame clears the bubble, and it stays cleared after switching back", async () => {
  renderScreen(withRepo());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  FakeEventSource.latest().emit("progress", progress({ status: "Using Read" }));
  FakeEventSource.latest().emit("progress", progress({}));
  expect(screen.queryByText("Using Read")).not.toBeInTheDocument();
  expect(screen.queryByRole("status")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*beta-2/ }));
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  expect(screen.queryByText("Using Read")).not.toBeInTheDocument();
});

it("shows the branch as a chip in the session list and in the details", async () => {
  renderScreen(withRepo());
  const row = screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ });
  expect(within(row).getByText("fyrd/alpha")).toBeInTheDocument();
  await userEvent.click(row);
  const details = screen.getByRole("complementary", { name: "Session details" });
  expect(within(details).getByText("Branch")).toBeInTheDocument();
  expect(within(details).getByText("fyrd/alpha")).toBeInTheDocument();
});

it("shows a workers-running chip and a collapsed worker result card", async () => {
  renderScreen(hubState({ sessions: [session({ id: "a", title: "alpha-1", status: "idle", tags: ["fyrd-hub", "fyrd-workers-running"], workersRunning: true })] }));
  expect(screen.getByText("workers running")).toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  expect(screen.getByText("Working (background)")).toBeInTheDocument();
  FakeEventSource.latest().emit("item", item({ kind: "worker", text: "build: initial task", detail: "fork", toolUseId: "tw1" }));
  FakeEventSource.latest().emit("item", item({ key: "2:0", seq: 2, kind: "worker-result", text: "Status: done", status: "completed", toolUseId: "tw1" }));
  expect(screen.getByText("build: initial task")).toBeInTheDocument();
  const card = screen.getByText("Worker result (completed)").closest("details") as HTMLElement;
  expect(card).not.toHaveAttribute("open");
  expect(within(card).getByText("Status: done")).toBeInTheDocument();
});

describe("export", () => {
  const okResponse = () =>
    new Response("{}\n", { status: 200, headers: { "Content-Disposition": 'attachment; filename="alpha-1-a-20261102-201000.jsonl"' } });

  beforeEach(() => {
    vi.mocked(api.exportUrl).mockImplementation((id: string) => `/api/hub/sessions/${id}/export`);
    URL.createObjectURL = vi.fn(() => "blob:x");
    URL.revokeObjectURL = vi.fn();
  });
  afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

  it("shows an Export button on every row, Controller included", () => {
    renderScreen(two());
    for (const t of ["controller", "alpha-1", "beta-2"])
      expect(screen.getByRole("button", { name: `Export ${t}` })).toBeInTheDocument();
  });

  it("click fetches the export URL and saves a file", async () => {
    const fetchMock = vi.fn().mockResolvedValue(okResponse());
    vi.stubGlobal("fetch", fetchMock);
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    renderScreen(two());
    await userEvent.click(screen.getByRole("button", { name: "Export alpha-1" }));
    await vi.waitFor(() => expect(click).toHaveBeenCalled());
    expect(fetchMock).toHaveBeenCalledWith("/api/hub/sessions/a/export");
  });

  it("shows the problem detail on failure", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ detail: "Signed out of Claude" }), { status: 502 })));
    renderScreen(two());
    await userEvent.click(screen.getByRole("button", { name: "Export alpha-1" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Signed out of Claude");
    expect(screen.getByRole("button", { name: "Export alpha-1" })).toBeEnabled();
  });

  it("disables the button while the request runs", async () => {
    let finish!: (r: Response) => void;
    vi.stubGlobal("fetch", vi.fn(() => new Promise<Response>((r) => { finish = r; })));
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    renderScreen(two());
    await userEvent.click(screen.getByRole("button", { name: "Export alpha-1" }));
    expect(screen.getByRole("button", { name: "Export alpha-1" })).toBeDisabled();
    finish(okResponse());
    await vi.waitFor(() => expect(screen.getByRole("button", { name: "Export alpha-1" })).toBeEnabled());
  });
});

describe("without GitHub access", () => {
  const openAlpha = async (gitHub: HubState["gitHub"], prs = true) => {
    const st = withRepo();
    st.gitHub = gitHub;
    if (!prs) st.sessions[0].pullRequests = [];
    vi.mocked(api.pullRequests).mockResolvedValue([prStatus({ draft: true, canMerge: true })]);
    renderScreen(st);
    await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
    return screen.getByRole("complementary", { name: "Session details" });
  };

  it("shows a note instead of statuses when no token is set, and hides ready and merge", async () => {
    const details = await openAlpha({ configured: false, login: null, error: null }, false);
    expect(within(details).getByRole("note")).toHaveTextContent("Set GH_TOKEN before /fyrd:up to see PR status and act on PRs.");
  });

  it("shows the token error the same way", async () => {
    const details = await openAlpha({ configured: true, login: null, error: "GitHub rejected the token (bad or expired)." });
    expect(within(details).getByRole("note")).toHaveTextContent("bad or expired");
    expect(within(details).queryByRole("button", { name: "Merge #12" })).not.toBeInTheDocument();
    expect(within(details).queryByRole("button", { name: "Mark #12 ready for review" })).not.toBeInTheDocument();
  });

  it("shows no note when signed in", async () => {
    const details = await openAlpha({ configured: true, login: "octocat", error: null });
    expect(within(details).queryByRole("note")).not.toBeInTheDocument();
  });
});

describe("pull request details", () => {
  it("shows checks with counts on a draft, and a link to the failing check", async () => {
    const { details } = await openAlphaWith(prStatus({
      draft: true, checks: "failing", canMerge: false, checksPassed: 8, checksFailed: 1, checksPending: 2,
      failingCheck: { name: "lint", url: "https://ci.example/lint" },
    }));
    expect(within(details).getByText("Draft")).toBeInTheDocument();
    const checks = within(details).getByTitle("Checks failing: lint");
    expect(checks).toHaveTextContent("✓ 8 ✗ 1 ◌ 2");
    const open = vi.spyOn(window, "open").mockImplementation(() => null);
    await userEvent.click(checks);
    expect(open).toHaveBeenCalledWith("https://ci.example/lint", "_blank", "noopener,noreferrer");
    open.mockRestore();
  });

  it("shows the title and the base branch, with the full title as a tooltip", async () => {
    const { details } = await openAlphaWith(prStatus({ title: "A rather long pull request title", base: "release" }));
    expect(within(details).getByText("A rather long pull request title")).toHaveAttribute("title", "A rather long pull request title");
    expect(within(details).getByText("→ release")).toBeInTheDocument();
  });

  it("flags conflicts and hides Merge", async () => {
    const { details } = await openAlphaWith(prStatus({ mergeable: "conflicting", canMerge: false }));
    expect(within(details).getByText("Conflicts")).toBeInTheDocument();
    expect(within(details).queryByRole("button", { name: "Merge #12" })).not.toBeInTheDocument();
  });

  it("flags a PR that is behind its base", async () => {
    const { details } = await openAlphaWith(prStatus({ mergeState: "behind" }));
    expect(within(details).getByText("Behind base")).toBeInTheDocument();
  });

  it.each([
    ["commented", "Reviewed"],
    ["approved", "Approved"],
    ["changes_requested", "Changes requested"],
  ] as const)("shows a %s review as %s", async (review, text) => {
    const { details } = await openAlphaWith(prStatus({ review }));
    expect(within(details).getByText(text)).toBeInTheDocument();
  });

  it("the merge confirm says squash and names the base", async () => {
    const { details } = await openAlphaWith(prStatus({ base: "main" }));
    await userEvent.click(within(details).getByRole("button", { name: "Merge #12" }));
    expect(within(details).getByText(/squash-merges #12 into/)).toHaveTextContent("squash-merges #12 into main on GitHub.");
  });

  it("a failed action shows its error under the PR", async () => {
    vi.mocked(api.markPrReady).mockRejectedValue(new Error("GitHub refused"));
    const { details } = await openAlphaWith(prStatus({ draft: true, canMerge: false }));
    await userEvent.click(within(details).getByRole("button", { name: "Mark #12 ready for review" }));
    expect(await within(details).findByText("GitHub refused")).toBeInTheDocument();
  });
});

describe("live PR statuses", () => {
  const listedStatus = (o: Partial<PullRequestStatus> = {}) => prStatus(o);
  const PR40 = "https://github.com/acme/widgets/pull/40";
  const stateWith = (sessions: HubState["sessions"], pullRequests: Record<string, PullRequestStatus>) =>
    hubState({ sessions, pullRequests });
  const rowOf = (name: string) => screen.getByRole("button", { name: new RegExp(`^(?!Archive|Rename|Export).*${name}`) });

  it("a row with one PR shows its number, a checks dot and a conflict marker from hub state", () => {
    renderScreen(stateWith(
      [session({ id: "a", title: "alpha-1", pullRequests: [PR12] })],
      { [PR12]: listedStatus({ checks: "failing", mergeable: "conflicting", canMerge: false }) },
    ));
    const row = rowOf("alpha-1");
    expect(within(row).getByText("#12")).toBeInTheDocument();
    expect(within(row).getByRole("img", { name: "Checks failing" })).toHaveClass("pr-dot-failing");
    expect(within(row).getByRole("img", { name: "Merge conflicts" })).toBeInTheDocument();
  });

  it("a merged PR shows no checks dot; a draft shows neither dot nor marker unless open with checks", () => {
    renderScreen(stateWith(
      [session({ id: "a", title: "alpha-1", pullRequests: [PR12] })],
      { [PR12]: listedStatus({ state: "merged", checks: "passing", canMerge: false }) },
    ));
    expect(rowOf("alpha-1").querySelector(".pr-dot, .pr-conflict")).toBeNull();
  });

  it("several PRs show the count with the worst status: failing > conflicts > pending > passing", () => {
    const urls = [PR12, PR40, "https://github.com/acme/widgets/pull/41"];
    renderScreen(stateWith(
      [session({ id: "a", title: "alpha-1", pullRequests: urls })],
      {
        [urls[0]]: listedStatus({ checks: "passing" }),
        [urls[1]]: listedStatus({ url: urls[1], number: 40, checks: "pending", canMerge: false }),
        [urls[2]]: listedStatus({ url: urls[2], number: 41, checks: "passing", mergeable: "conflicting", canMerge: false }),
      },
    ));
    const row = rowOf("alpha-1");
    expect(within(row).getByText("3 PRs")).toBeInTheDocument();
    expect(within(row).getByTitle(/#40 \(open, checks running\)/)).toBeInTheDocument();
    expect(within(row).getByRole("img", { name: "Merge conflicts" })).toBeInTheDocument();
    expect(within(row).queryByRole("img", { name: "Checks failing" })).not.toBeInTheDocument();
  });

  it("the details pane shows hub-state statuses at once, with a stacked tag", async () => {
    vi.mocked(api.pullRequests).mockImplementation(() => new Promise(() => {})); // the live read has not answered yet
    const st = stateWith(
      [session({ id: "a", title: "alpha-1", repo: "acme/widgets", pullRequests: [PR12, PR40], stackedPullRequests: [PR40] })],
      {
        [PR12]: listedStatus({ title: "Own PR", checksPassed: 2 }),
        [PR40]: listedStatus({ url: PR40, number: 40, title: "Stacked PR", canMerge: false }),
      },
    );
    renderScreen(st);
    await userEvent.click(rowOf("alpha-1"));
    const details = screen.getByRole("complementary", { name: "Session details" });
    expect(within(details).getByText("Own PR")).toBeInTheDocument();
    expect(within(details).getByText("Stacked PR")).toBeInTheDocument();
    expect(within(details).getAllByText("stacked")).toHaveLength(1);
  });

  it("lists the other sessions that own the PR under it, and links to them", async () => {
    vi.mocked(api.pullRequests).mockResolvedValue([prStatus()]);
    const st = hubState({
      sessions: [
        session({ id: "a", title: "alpha-1", role: "builder", pullRequests: [PR12] }),
        session({ id: "v", title: "validator-3", role: "validator", status: "idle", pullRequests: [PR12] }),
        session({ id: "r", title: "reviewer-2", role: "reviewer", status: "running", pullRequests: [PR12] }),
        session({ id: "z", title: "zeta-9", pullRequests: [] }),
      ],
    });
    renderScreen(st);
    await userEvent.click(rowOf("alpha-1"));
    const details = screen.getByRole("complementary", { name: "Session details" });
    await within(details).findByRole("button", { name: "Open #12" });
    expect(within(details).getByRole("button", { name: "Open validator-3" })).toHaveTextContent("validator-3 · idle");
    expect(within(details).getByRole("button", { name: "Open reviewer-2" })).toHaveTextContent("reviewer-2 · running");
    expect(within(details).queryByRole("button", { name: "Open zeta-9" })).not.toBeInTheDocument();
    expect(within(details).queryByRole("button", { name: "Open alpha-1" })).not.toBeInTheDocument();
  });

  describe("30 s refresh", () => {
    afterEach(() => { vi.useRealTimers(); });

    const pendingState = () => stateWith(
      [session({ id: "a", title: "alpha-1", repo: "acme/widgets", pullRequests: [PR12] })],
      { [PR12]: listedStatus({ checks: "pending", canMerge: false }) },
    );

    it("re-reads every 30 s while a PR has pending checks, and Merge appears once green", async () => {
      vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
      vi.mocked(api.pullRequests).mockReset();
      vi.mocked(api.pullRequests)
        .mockResolvedValueOnce([prStatus({ checks: "pending", canMerge: false })])
        .mockResolvedValue([prStatus({ checks: "passing", canMerge: true })]);
      renderScreen(pendingState());
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      await user.click(rowOf("alpha-1"));
      const details = screen.getByRole("complementary", { name: "Session details" });
      await within(details).findByRole("button", { name: "Open #12" });
      expect(within(details).queryByRole("button", { name: "Merge #12" })).not.toBeInTheDocument();
      expect(api.pullRequests).toHaveBeenCalledTimes(1);

      await act(async () => { vi.advanceTimersByTime(29_000); });
      expect(api.pullRequests).toHaveBeenCalledTimes(1);
      await act(async () => { vi.advanceTimersByTime(1_000); });
      expect(api.pullRequests).toHaveBeenCalledTimes(2);
      expect(await within(details).findByRole("button", { name: "Merge #12" })).toBeInTheDocument();

      // Green now: no further polling.
      await act(async () => { vi.advanceTimersByTime(120_000); });
      expect(api.pullRequests).toHaveBeenCalledTimes(2);
    });

    it("does not poll when no PR has pending checks", async () => {
      vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
      vi.mocked(api.pullRequests).mockReset();
      vi.mocked(api.pullRequests).mockResolvedValue([prStatus({ checks: "passing" })]);
      renderScreen(stateWith(
        [session({ id: "a", title: "alpha-1", repo: "acme/widgets", pullRequests: [PR12] })],
        { [PR12]: listedStatus({ checks: "passing" }) },
      ));
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      await user.click(rowOf("alpha-1"));
      await screen.findByRole("button", { name: "Open #12" });
      await act(async () => { vi.advanceTimersByTime(120_000); });
      expect(api.pullRequests).toHaveBeenCalledTimes(1);
    });
  });
});

describe("PR nudges", () => {
  const stateFor = (o: { controller?: boolean } = {}) => hubState({
    sessions: [
      session({ id: "a", title: "alpha-1", role: "builder", repo: "acme/widgets", branch: "fyrd/alpha", pullRequests: [PR12] }),
      session({ id: "c", title: "controller", role: "controller", tags: [] }),
    ],
    controller: o.controller === false ? null : { sessionId: "c", url: "https://claude.ai/code/c" },
  });
  const openMenu = async (pr: PullRequestStatus, st = stateFor()) => {
    vi.mocked(api.pullRequests).mockResolvedValue([pr]);
    vi.mocked(api.sendMessage).mockResolvedValue(undefined);
    renderScreen(st);
    await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
    const details = screen.getByRole("complementary", { name: "Session details" });
    await userEvent.click(await within(details).findByRole("button", { name: "Ask for help with #12" }));
    return details;
  };

  it("Request validation sends the exact text to the Controller and says so", async () => {
    const details = await openMenu(prStatus());
    await userEvent.click(await screen.findByRole("menuitem", { name: "Request validation" }));
    expect(api.sendMessage).toHaveBeenCalledWith("c", `Validate PR ${PR12}. Base branch: main. Builder: alpha-1 (a).`);
    expect(await within(details).findByText("Sent to controller")).toBeInTheDocument();
  });

  it("a rapid double send of one nudge messages the session once", async () => {
    let release: () => void = () => {};
    const details = await openMenu(prStatus());
    vi.mocked(api.sendMessage).mockImplementation(() => new Promise<undefined>((r) => { release = () => r(undefined); }));
    const trigger = within(details).getByRole("button", { name: "Ask for help with #12" });
    const menuId = trigger.getAttribute("aria-controls")!;
    const item = () => within(document.getElementById(menuId)!).getByRole("menuitem", { name: "Request validation" });
    await userEvent.click(item());
    await userEvent.click(trigger);
    await userEvent.click(item());
    expect(api.sendMessage).toHaveBeenCalledTimes(1);
    release();
    await within(details).findByText("Sent to controller");
  });

  it("Request review is not offered on a draft", async () => {
    await openMenu(prStatus({ draft: true, canMerge: false }));
    expect(await screen.findByRole("menuitem", { name: "Request validation" })).toBeInTheDocument();
    expect(screen.queryByRole("menuitem", { name: "Request review" })).not.toBeInTheDocument();
  });

  it("Fix failing checks goes to the owning Builder, not the Controller", async () => {
    await openMenu(prStatus({ checks: "failing", canMerge: false, failingCheck: { name: "lint", url: null } }));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Fix failing checks" }));
    expect(api.sendMessage).toHaveBeenCalledWith("a", `Checks are failing on ${PR12}: lint. Fix them, run the tests, push, and reply as before.`);
  });

  it("the Controller items are disabled without a Controller", async () => {
    await openMenu(prStatus(), stateFor({ controller: false }));
    const item = await screen.findByRole("menuitem", { name: "Request review" });
    expect(item).toHaveAttribute("aria-disabled", "true");
    expect(item).toHaveAttribute("title", "No Controller");
    await userEvent.click(item);
    expect(api.sendMessage).not.toHaveBeenCalled();
  });

  it("Watch PR asks first, and sends only after the confirm", async () => {
    const details = await openMenu(prStatus());
    await userEvent.click(await screen.findByRole("menuitem", { name: "Watch PR" }));
    expect(api.sendMessage).not.toHaveBeenCalled();
    expect(within(details).getByText("Start a PR Monitor for #12?")).toBeInTheDocument();
    await userEvent.click(within(details).getByRole("button", { name: "Start PR Monitor" }));
    expect(api.sendMessage).toHaveBeenCalledWith("c", `Watch ${PR12} and fix anything that comes up`);
  });

  it("Watch PR can be cancelled", async () => {
    const details = await openMenu(prStatus());
    await userEvent.click(await screen.findByRole("menuitem", { name: "Watch PR" }));
    await userEvent.click(within(details).getByRole("button", { name: "Cancel" }));
    expect(api.sendMessage).not.toHaveBeenCalled();
    expect(within(details).queryByText("Start a PR Monitor for #12?")).not.toBeInTheDocument();
  });

  it("a failed send shows its error under the PR", async () => {
    const details = await openMenu(prStatus());
    vi.mocked(api.sendMessage).mockRejectedValue(new Error("Controller is busy"));
    await userEvent.click(await screen.findByRole("menuitem", { name: "Request validation" }));
    expect(await within(details).findByText("Controller is busy")).toBeInTheDocument();
  });
});

it("the in-progress bubble lists running workers with their latest tool", async () => {
  renderScreen(two());
  await userEvent.click(screen.getByRole("button", { name: /^(?!Archive|Rename|Export).*alpha-1/ }));
  FakeEventSource.latest().emit("progress", item({
    key: "ephemeral", seq: 0, kind: "progress", text: "", status: "Waiting for workers",
    workers: [{ id: "tw1", description: "build: initial task", line: "Running Bash · 42s" }],
  }));
  const list = screen.getByRole("list", { name: "Running workers" });
  expect(within(list).getByText("build: initial task")).toBeInTheDocument();
  expect(within(list).getByText(/Running Bash · 42s/)).toBeInTheDocument();
});
