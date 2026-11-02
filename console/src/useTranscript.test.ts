import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { FakeEventSource, item } from "./test/fakeEventSource";
import { clearTranscriptCache, DEFAULT_ITEM_LIMIT, useTranscript } from "./useTranscript";

beforeEach(() => {
  FakeEventSource.instances = [];
  clearTranscriptCache();
  vi.stubGlobal("EventSource", FakeEventSource);
});
afterEach(() => vi.unstubAllGlobals());

it("restores a session's loaded conversation from cache when it is selected again", () => {
  const { result, rerender } = renderHook(({ id }) => useTranscript(id), { initialProps: { id: "a" as string | null } });
  const first = item({ text: "one" });
  FakeEventSource.latest().emit("item", first);
  rerender({ id: "b" });
  expect(result.current.items).toEqual([]);
  rerender({ id: "a" });
  expect(result.current.items).toEqual([first]); // present before the new stream sends anything
});

it("does not duplicate cached items when the server replays the transcript", () => {
  const { result, rerender } = renderHook(({ id }) => useTranscript(id), { initialProps: { id: "a" as string | null } });
  const first = item({ text: "one" });
  FakeEventSource.latest().emit("item", first);
  rerender({ id: "b" });
  rerender({ id: "a" });
  const next = item({ text: "two" });
  FakeEventSource.latest().emit("item", first);
  FakeEventSource.latest().emit("item", next);
  expect(result.current.items.map((i) => i.key)).toEqual([first.key, next.key]);
});

it("renders only the newest items by default and loads more on request", () => {
  const { result } = renderHook(() => useTranscript("a"));
  const total = DEFAULT_ITEM_LIMIT * 2 + 5;
  const all = Array.from({ length: total }, (_, i) => item({ text: `m${i}` }));
  for (const it of all) FakeEventSource.latest().emit("item", it);
  expect(result.current.items).toHaveLength(DEFAULT_ITEM_LIMIT);
  expect(result.current.items.at(-1)).toEqual(all.at(-1));
  expect(result.current.hiddenCount).toBe(total - DEFAULT_ITEM_LIMIT);
  act(() => result.current.loadMore());
  expect(result.current.items).toHaveLength(DEFAULT_ITEM_LIMIT * 2);
  act(() => result.current.loadMore());
  expect(result.current.items).toHaveLength(total);
  expect(result.current.hiddenCount).toBe(0);
});

it("resets the window when switching sessions", () => {
  const { result, rerender } = renderHook(({ id }) => useTranscript(id), { initialProps: { id: "a" } });
  for (let i = 0; i < DEFAULT_ITEM_LIMIT + 10; i++) FakeEventSource.latest().emit("item", item());
  act(() => result.current.loadMore());
  expect(result.current.hiddenCount).toBe(0);
  rerender({ id: "b" });
  rerender({ id: "a" });
  expect(result.current.items).toHaveLength(DEFAULT_ITEM_LIMIT);
  expect(result.current.hiddenCount).toBe(10);
});
