import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { run } from "../test/fixtures";
import NewSpecForm from "./NewSpecForm";

vi.mock("../api");

beforeEach(() => vi.resetAllMocks());

it("uploading a .md fills the textarea", async () => {
  render(<NewSpecForm onCreated={() => {}} onCancel={() => {}} />);
  await userEvent.upload(screen.getByLabelText("Upload .md"), new File(["# Hi"], "s.md", { type: "text/markdown" }));
  await waitFor(() => expect(screen.getByLabelText("Markdown")).toHaveValue("# Hi"));
});

it("create posts title, repo, markdown", async () => {
  vi.mocked(api.createSpec).mockResolvedValue(run());
  const onCreated = vi.fn();
  render(<NewSpecForm onCreated={onCreated} onCancel={() => {}} />);
  await userEvent.type(screen.getByLabelText("Title"), "My spec");
  await userEvent.type(screen.getByLabelText("Markdown"), "body");
  await userEvent.click(screen.getByRole("button", { name: "Create" }));
  expect(api.createSpec).toHaveBeenCalledWith({
    title: "My spec", repo: "servicetitan/titan-lens-ossie", markdown: "body",
  });
  await waitFor(() => expect(onCreated).toHaveBeenCalled());
});

it("shows the server error when create fails", async () => {
  vi.mocked(api.createSpec).mockRejectedValue(new Error("title required"));
  render(<NewSpecForm onCreated={() => {}} onCancel={() => {}} />);
  await userEvent.click(screen.getByRole("button", { name: "Create" }));
  expect(await screen.findByText("title required")).toBeInTheDocument();
});
