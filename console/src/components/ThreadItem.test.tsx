import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { MessageTime } from "./ThreadItem";

const now = Date.parse("2026-11-09T12:00:00Z");
const ago = (min: number) => new Date(now - min * 60_000).toISOString();

describe("MessageTime", () => {
  it("shows nothing for a message 60 minutes old or newer", () => {
    const { container } = render(<MessageTime at={ago(60)} now={now} />);
    expect(container).toBeEmptyDOMElement();
  });
  it("shows nothing without a valid time", () => {
    const { container } = render(<MessageTime at="nope" now={now} />);
    expect(container).toBeEmptyDOMElement();
  });
  it("shows a relative time with the full time as tooltip once older than 60 minutes", () => {
    render(<MessageTime at={ago(61)} now={now} />);
    const t = screen.getByText("1 hour ago");
    expect(t).toHaveAttribute("title");
    expect(t).toHaveClass("msg-time");
  });
});
