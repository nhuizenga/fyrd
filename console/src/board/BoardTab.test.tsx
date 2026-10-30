import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { entry, unit } from "../test/fixtures";
import BoardTab from "./BoardTab";

vi.mock("../api");

beforeEach(() => vi.resetAllMocks());

const renderBoard = (specs: Parameters<typeof BoardTab>[0]["specs"], onChange = () => {}) =>
  render(<BoardTab specs={specs} config={{ squadCap: 3 }} onChange={onChange} />);

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

it("squad cap save puts config and shows server error on 400", async () => {
  vi.mocked(api.putConfig).mockRejectedValue(new Error("squadCap must be 1-8"));
  renderBoard([]);
  const input = screen.getByLabelText("Squad cap");
  expect(input).toHaveValue(3);
  await userEvent.clear(input);
  await userEvent.type(input, "9");
  await userEvent.click(screen.getByRole("button", { name: "Save" }));
  expect(api.putConfig).toHaveBeenCalledWith({ squadCap: 9 });
  expect(await screen.findByText("squadCap must be 1-8")).toBeInTheDocument();
});

it("New spec button opens the form", async () => {
  renderBoard([]);
  await userEvent.click(screen.getByRole("button", { name: "New spec" }));
  expect(screen.getByLabelText("Repo")).toHaveValue("servicetitan/titan-lens-ossie");
});
