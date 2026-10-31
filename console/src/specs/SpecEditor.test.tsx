import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { run } from "../test/fixtures";
import SpecEditor from "./SpecEditor";

vi.mock("../api");

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(api.getSpec).mockResolvedValue({ run: run(), markdown: "# Old", units: [] });
});

it("saves the edited markdown and shows server detail on failure", async () => {
  const onSaved = vi.fn();
  vi.mocked(api.updateMarkdown).mockRejectedValueOnce(new Error("Markdown is required."));
  render(<SpecEditor specId="s1" onClose={() => {}} onSaved={onSaved} />);
  const box = await screen.findByRole("textbox", { name: "Spec markdown" });
  expect(box).toHaveValue("# Old");
  await userEvent.click(screen.getByRole("button", { name: "Save" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Markdown is required.");
  vi.mocked(api.updateMarkdown).mockResolvedValue(run());
  await userEvent.type(box, " more");
  await userEvent.click(screen.getByRole("button", { name: "Save" }));
  await waitFor(() => expect(api.updateMarkdown).toHaveBeenLastCalledWith("s1", "# Old more"));
  expect(onSaved).toHaveBeenCalled();
});
