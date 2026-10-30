import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { vi } from "vitest";
import * as api from "../api";
import { card } from "../test/fixtures";
import InboxCard from "./InboxCard";

vi.mock("../api");
const answer = vi.mocked(api.answer);
const c = card({ id: "c9", question: "SSO provider?", options: ["Entra ID", "Okta"], allowFreeText: true });

beforeEach(() => {
  answer.mockReset();
});

it("one click answers with remember=true scope=Repo by default", async () => {
  answer.mockResolvedValue(c);
  const onAnswered = vi.fn();
  render(<InboxCard card={c} specLabel="Spec One" onAnswered={onAnswered} />);
  await userEvent.click(screen.getByRole("button", { name: "Entra ID" }));
  expect(answer).toHaveBeenCalledWith("c9", { text: "Entra ID", remember: true, scope: "Repo" });
  await waitFor(() => expect(onAnswered).toHaveBeenCalled());
});

it("disables buttons while the answer is in flight", async () => {
  let resolve!: (v: typeof c) => void;
  answer.mockReturnValue(new Promise((r) => (resolve = r)));
  render(<InboxCard card={c} specLabel="Spec One" onAnswered={() => {}} />);
  const btn = screen.getByRole("button", { name: "Entra ID" });
  await userEvent.dblClick(btn);
  expect(answer).toHaveBeenCalledTimes(1);
  expect(btn).toBeDisabled();
  expect(screen.getByRole("button", { name: "Okta" })).toBeDisabled();
  resolve(c);
  await waitFor(() => expect(btn).toBeEnabled());
});

it("shows lead's pick and reason", () => {
  render(
    <InboxCard card={{ ...c, recommendation: "Entra ID", reason: "other repos use it" }}
      specLabel="Spec One" onAnswered={() => {}} />,
  );
  expect(screen.getByText("Lead's pick: Entra ID — other repos use it")).toBeInTheDocument();
});

it("expands details", async () => {
  render(<InboxCard card={{ ...c, details: "long story" }} specLabel="S" onAnswered={() => {}} />);
  expect(screen.queryByText("long story")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Details" }));
  expect(screen.getByText("long story")).toBeInTheDocument();
});

it("free text sends typed answer when allowed", async () => {
  answer.mockResolvedValue(c);
  render(<InboxCard card={c} specLabel="S" onAnswered={() => {}} />);
  await userEvent.type(screen.getByRole("textbox"), "Auth0");
  await userEvent.click(screen.getByRole("button", { name: "Send" }));
  expect(answer).toHaveBeenCalledWith("c9", { text: "Auth0", remember: true, scope: "Repo" });
});

it("hides free text when not allowed", () => {
  render(<InboxCard card={{ ...c, allowFreeText: false }} specLabel="S" onAnswered={() => {}} />);
  expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
});

it("unchecking remember sends remember=false; scope select sends Global", async () => {
  answer.mockResolvedValue(c);
  render(<InboxCard card={c} specLabel="S" onAnswered={() => {}} />);
  await userEvent.selectOptions(screen.getByRole("combobox"), "Global");
  await userEvent.click(screen.getByLabelText("Remember this"));
  await userEvent.click(screen.getByRole("button", { name: "Okta" }));
  expect(answer).toHaveBeenCalledWith("c9", { text: "Okta", remember: false, scope: "Global" });
});

it("shows the error when answering fails", async () => {
  answer.mockRejectedValue(new Error("already answered"));
  render(<InboxCard card={c} specLabel="S" onAnswered={() => {}} />);
  await userEvent.click(screen.getByRole("button", { name: "Okta" }));
  expect(await screen.findByText("already answered")).toBeInTheDocument();
});

it("renders details URLs as links and other text as text", async () => {
  const withDetails = card({ details: "See https://example.com/doc for <b>context</b>", options: ["A"] });
  render(<InboxCard card={withDetails} specLabel="S" onAnswered={() => {}} />);
  await userEvent.click(screen.getByRole("button", { name: "Details" }));
  const link = screen.getByRole("link", { name: "https://example.com/doc" });
  expect(link).toHaveAttribute("href", "https://example.com/doc");
  expect(link).toHaveAttribute("rel", "noreferrer");
  expect(screen.getByText(/<b>context<\/b>/)).toBeInTheDocument();
});

it("passes the relayError from the answer response to onAnswered", async () => {
  answer.mockResolvedValue({ ...c, relayError: "lead unreachable" });
  const onAnswered = vi.fn();
  render(<InboxCard card={c} specLabel="S" onAnswered={onAnswered} />);
  await userEvent.click(screen.getByRole("button", { name: "Okta" }));
  await waitFor(() => expect(onAnswered).toHaveBeenCalledWith("lead unreachable"));
});

it("attention cards default Remember off and hide the scope select", async () => {
  answer.mockResolvedValue(c);
  const att = { ...c, sourceKey: "s1:attention:e1" };
  render(<InboxCard card={att} specLabel="Spec One" onAnswered={() => {}} />);
  expect(screen.getByRole("checkbox", { name: /remember/i })).not.toBeChecked();
  expect(screen.queryByLabelText("Memory scope")).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole("button", { name: "Entra ID" }));
  expect(answer).toHaveBeenCalledWith("c9", { text: "Entra ID", remember: false, scope: "Repo" });
});
