import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { boardSpec, config, entry, unit } from "../test/fixtures";
import type { BoardSpec } from "../types";
import BoardTab from "./BoardTab";

vi.mock("../api");

beforeEach(() => vi.resetAllMocks());

const renderBoard = (
  specs: Array<Partial<BoardSpec>>, onChange = () => {}, onRelayError: (m: string) => void = () => {},
) => render(<BoardTab specs={specs.map((s) => boardSpec(s))} config={config()} onChange={onChange} onRelayError={onRelayError} />);

it("renders stage markers per unit", () => {
  renderBoard([{
    entry: entry(),
    units: [
      unit({ id: "u1", title: "Impl", stage: "Implement", state: "Running" }),
      unit({ id: "u2", title: "Val", stage: "Validate", state: "Idle" }),
      unit({ id: "u3", title: "Fin", stage: "Done", state: "Done" }),
      unit({ id: "u4", title: "Wait", stage: "Review", state: "WaitingOnOwner" }),
    ],
  }]);
  expect(screen.getByText("●○○")).toBeInTheDocument();
  expect(screen.getByText("✓✓●")).toBeInTheDocument();
  expect(screen.getByText("✓✓✓")).toBeInTheDocument();
  expect(screen.getByText("waiting on you")).toBeInTheDocument();
});

it("shows malformed spec as error row", () => {
  renderBoard([{ entry: { run: null, id: "bad", error: "bad frontmatter" }, units: [] }]);
  expect(screen.getByText(/bad frontmatter/)).toBeInTheDocument();
});

it("attack button only on Draft specs and calls attack(id)", async () => {
  vi.mocked(api.attack).mockResolvedValue(entry().run!);
  const onChange = vi.fn();
  renderBoard([
    { entry: entry({ id: "d1", title: "Drafty", status: "Draft" }), units: [] },
    { entry: entry({ id: "b1", title: "Busy", status: "Building" }), units: [] },
  ], onChange);
  const buttons = screen.getAllByRole("button", { name: "Attack" });
  expect(buttons).toHaveLength(1);
  await userEvent.click(buttons[0]);
  expect(api.attack).toHaveBeenCalledWith("d1");
  await waitFor(() => expect(onChange).toHaveBeenCalled());
});

it("squad cap lives in Settings: save puts config and shows server error on 400", async () => {
  vi.mocked(api.putConfig).mockRejectedValue(new Error("squadCap must be 1-8"));
  renderBoard([]);
  expect(screen.queryByLabelText("Squad cap")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Settings" }));
  const input = screen.getByLabelText("Squad cap");
  expect(input).toHaveValue(3);
  await userEvent.clear(input);
  await userEvent.type(input, "9");
  await userEvent.click(screen.getByRole("button", { name: "Save" }));
  expect(api.putConfig).toHaveBeenCalledWith({
    squadCap: 9, environmentId: null, leadModel: "claude-opus-5-5", peerModel: "claude-sonnet-5-5",
  });
  expect(await screen.findByText("squadCap must be 1-8")).toBeInTheDocument();
});

it("New spec button opens the form", async () => {
  renderBoard([]);
  await userEvent.click(screen.getByRole("button", { name: "New spec" }));
  expect(screen.getByLabelText("Repo")).toHaveValue("servicetitan/titan-lens-ossie");
});

it("shows attack 409 detail inline", async () => {
  vi.mocked(api.attack).mockRejectedValue(new Error("Set the cloud environment in Settings"));
  renderBoard([{ entry: entry({ id: "d1", status: "Draft" }) }]);
  await userEvent.click(screen.getByRole("button", { name: "Attack" }));
  expect(await screen.findByText("Set the cloud environment in Settings")).toBeInTheDocument();
});

it("renders peer session links per unit, by role", () => {
  renderBoard([{
    units: [unit({ id: "u1", title: "One" }), unit({ id: "u2", title: "Two" })],
    peers: [
      { sessionId: "session_a", url: "https://claude.ai/code/session_a", role: "implementer", unitId: "u1", status: "running" },
      { sessionId: "session_b", url: "https://claude.ai/code/session_b", role: "reviewer", unitId: "u1", status: "idle" },
      { sessionId: "session_c", url: "https://claude.ai/code/session_c", role: "validator", unitId: "u2", status: "idle" },
    ],
  }]);
  const impl = screen.getByRole("link", { name: "implementer" });
  expect(impl).toHaveAttribute("href", "https://claude.ai/code/session_a");
  expect(impl).toHaveAttribute("target", "_blank");
  expect(impl).toHaveAttribute("rel", "noreferrer");
  expect(screen.getByRole("link", { name: "reviewer" })).toHaveAttribute("href", "https://claude.ai/code/session_b");
  expect(screen.getByText("One").closest("li")).not.toHaveTextContent("validator");
  expect(screen.getByText("Two").closest("li")).toHaveTextContent("validator");
});

it("renders lead link, a context bar and the branch", () => {
  renderBoard([{
    entry: entry({ status: "ReadyForHandoff", branch: "fyrd/export" }),
    lead: { sessionId: "session_l", url: "https://claude.ai/code/session_l", status: "running", contextUsed: 52000, contextMax: 1000000 },
  }]);
  expect(screen.getByRole("link", { name: /Lead/ })).toHaveAttribute("href", "https://claude.ai/code/session_l");
  expect(screen.getByText("52k / 1M tokens")).toBeInTheDocument();
  expect(screen.getByRole("progressbar", { name: "Lead context usage" })).toHaveAttribute("value", "52000");
  expect(screen.getByText("fyrd/export")).toBeInTheDocument();
});

it("renders untrusted titles as text, not HTML", () => {
  renderBoard([{ entry: entry({ title: "<img src=x onerror=alert(1)>" }) }]);
  expect(screen.getByText("<img src=x onerror=alert(1)>")).toBeInTheDocument();
  expect(document.querySelector("img")).toBeNull();
});

describe("research", () => {
  it("Research on a Draft calls the API; Attack is enabled without a proposal", async () => {
    vi.mocked(api.research).mockResolvedValue(entry().run!);
    const onChange = vi.fn();
    renderBoard([{ entry: entry({ status: "Draft" }) }], onChange);
    expect(screen.getByRole("button", { name: "Attack" })).toBeEnabled();
    await userEvent.click(screen.getByRole("button", { name: "Research" }));
    await waitFor(() => expect(api.research).toHaveBeenCalledWith("s1"));
    expect(onChange).toHaveBeenCalled();
  });

  it("Attack is disabled with a hint when a proposal is waiting; Review proposal opens the review", async () => {
    vi.mocked(api.getSpec).mockResolvedValue({ run: entry().run!, markdown: "# Cur", units: [] });
    vi.mocked(api.getProposal).mockResolvedValue({ markdown: "# Prop", summary: "Sum", openQuestions: [], sessionId: null });
    renderBoard([{ entry: entry({ status: "Draft", hasProposal: true }) }]);
    expect(screen.getByRole("button", { name: "Attack" })).toBeDisabled();
    expect(screen.getByText("Review the research proposal first")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Review proposal" }));
    expect(await screen.findByText("# Prop")).toBeInTheDocument();
  });

  it("shows research errors inline", async () => {
    vi.mocked(api.research).mockRejectedValue(new Error("Set the cloud environment in Settings"));
    renderBoard([{ entry: entry({ status: "Draft" }) }]);
    await userEvent.click(screen.getByRole("button", { name: "Research" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Set the cloud environment in Settings");
  });

  it("Researching units show the diamond marker", () => {
    renderBoard([{
      entry: entry({ status: "Researching" }),
      units: [1, 2, 3].map((n) => unit({ id: `r${n}`, title: `Angle ${n}`, stage: "Research" })),
    }]);
    expect(screen.getAllByText("◆")).toHaveLength(3);
    expect(screen.queryByRole("button", { name: "Research" })).not.toBeInTheDocument();
  });

  it("Edit spec is Draft only and opens the editor", async () => {
    vi.mocked(api.getSpec).mockResolvedValue({ run: entry().run!, markdown: "# Body", units: [] });
    const { unmount } = renderBoard([{ entry: entry({ status: "Building" }) }]);
    expect(screen.queryByRole("button", { name: "Edit spec" })).not.toBeInTheDocument();
    unmount();
    renderBoard([{ entry: entry({ status: "Draft" }) }]);
    await userEvent.click(screen.getByRole("button", { name: "Edit spec" }));
    expect(await screen.findByRole("textbox", { name: "Spec markdown" })).toHaveValue("# Body");
  });
});
