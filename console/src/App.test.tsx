import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "./api";
import App from "./App";
import { card, entry, unit } from "./test/fixtures";

vi.mock("./api");

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(api.getInbox).mockResolvedValue([]);
  vi.mocked(api.getSpecs).mockResolvedValue([]);
  vi.mocked(api.getConfig).mockResolvedValue({ squadCap: 3 });
});

it("opens on the Inbox tab with the open-card count", async () => {
  vi.mocked(api.getInbox).mockResolvedValue([card({ question: "SSO provider?" })]);
  render(<App />);
  expect(await screen.findByRole("tab", { name: "Inbox (1)", selected: true })).toBeInTheDocument();
  expect(screen.getByText("SSO provider?")).toBeInTheDocument();
});

it("Board tab shows done count and spec groups", async () => {
  vi.mocked(api.getSpecs).mockResolvedValue([entry({ id: "s1", title: "Spec One" })]);
  vi.mocked(api.getSpec).mockResolvedValue({
    run: entry().run!, markdown: "",
    units: [unit({ id: "a", stage: "Done", state: "Done" }), unit({ id: "b" })],
  });
  render(<App />);
  const tab = await screen.findByRole("tab", { name: "Board · 1/2 done" });
  await userEvent.click(tab);
  expect(screen.getByText("Spec One")).toBeInTheDocument();
  expect(screen.getAllByText(/^[✓●○]{3}$/)).toHaveLength(2);
});

it("one failing endpoint does not freeze the other data", async () => {
  vi.mocked(api.getInbox).mockRejectedValue(new Error("boom"));
  vi.mocked(api.getSpecs).mockResolvedValue([entry({ id: "s1", title: "Spec One" })]);
  vi.mocked(api.getSpec).mockResolvedValue({ run: entry().run!, markdown: "", units: [unit({ id: "a" })] });
  render(<App />);
  await userEvent.click(await screen.findByRole("tab", { name: /^Board · 0\/1/ }));
  expect(screen.getByText("Spec One")).toBeInTheDocument();
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
});

it("shows the unreachable banner only when every endpoint fails", async () => {
  vi.mocked(api.getInbox).mockRejectedValue(new Error("down"));
  vi.mocked(api.getSpecs).mockRejectedValue(new Error("down"));
  vi.mocked(api.getConfig).mockRejectedValue(new Error("down"));
  render(<App />);
  expect(await screen.findByRole("alert")).toHaveTextContent("Can't reach the Fyrd server");
});
