import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import MemoryTab from "./MemoryTab";

vi.mock("../api");

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(api.getMemory).mockResolvedValue({
    ownerProxy: "I prefer small PRs", global: "Use xunit", repos: [{ repo: "servicetitan/r", text: "repo notes" }],
  });
});

it("renders sections and the apply-from-now note", async () => {
  render(<MemoryTab />);
  expect(await screen.findByRole("textbox", { name: "Owner preferences" })).toHaveValue("I prefer small PRs");
  expect(screen.getByRole("textbox", { name: "Global decisions" })).toHaveValue("Use xunit");
  expect(screen.getByRole("textbox", { name: "servicetitan/r" })).toHaveValue("repo notes");
  expect(screen.getByText("Changes apply to specs launched from now on.")).toBeInTheDocument();
});

it("saves each section to its endpoint", async () => {
  vi.mocked(api.saveMemory).mockResolvedValue(undefined);
  render(<MemoryTab />);
  const box = await screen.findByRole("textbox", { name: "servicetitan/r" });
  await userEvent.type(box, "!");
  await userEvent.click(screen.getByRole("button", { name: "Save servicetitan/r" }));
  await waitFor(() => expect(api.saveMemory).toHaveBeenCalledWith("repo", "repo notes!", "servicetitan/r"));
  await userEvent.click(screen.getByRole("button", { name: "Save Global decisions" }));
  await waitFor(() => expect(api.saveMemory).toHaveBeenCalledWith("global", "Use xunit", undefined));
  await userEvent.click(screen.getByRole("button", { name: "Save Owner preferences" }));
  await waitFor(() => expect(api.saveMemory).toHaveBeenCalledWith("owner-proxy", "I prefer small PRs", undefined));
});

it("shows the 400 detail on a failed save", async () => {
  vi.mocked(api.saveMemory).mockRejectedValue(new Error("Text is larger than 200 KB."));
  render(<MemoryTab />);
  await screen.findByRole("textbox", { name: "Global decisions" });
  await userEvent.click(screen.getByRole("button", { name: "Save Global decisions" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Text is larger than 200 KB.");
});
