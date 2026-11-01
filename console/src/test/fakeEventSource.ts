import { act } from "@testing-library/react";
import type { TranscriptItem } from "../types";

/** jsdom has no EventSource; this stand-in records instances so tests can push frames into them. */
export class FakeEventSource {
  static instances: FakeEventSource[] = [];
  onopen: (() => void) | null = null;
  closed = false;
  private listeners = new Map<string, ((e: MessageEvent<string>) => void)[]>();

  constructor(public url: string) {
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, fn: (e: MessageEvent<string>) => void) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), fn]);
  }

  close() {
    this.closed = true;
  }

  emit(type: string, data: unknown) {
    act(() => {
      for (const fn of this.listeners.get(type) ?? []) fn(new MessageEvent(type, { data: JSON.stringify(data) }));
    });
  }

  static latest(): FakeEventSource {
    const s = FakeEventSource.instances[FakeEventSource.instances.length - 1];
    if (!s) throw new Error("No EventSource was opened.");
    return s;
  }
}

let seq = 0;
export const item = (o: Partial<TranscriptItem> = {}): TranscriptItem => {
  seq += 1;
  return { key: `${seq}:0`, seq, role: "agent", kind: "text", text: "hi", at: "2026-11-01T00:00:00Z", ...o };
};
