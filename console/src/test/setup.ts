import "@testing-library/jest-dom/vitest";
import { FakeEventSource } from "./fakeEventSource";

globalThis.EventSource = FakeEventSource as unknown as typeof EventSource;
beforeEach(() => {
  FakeEventSource.instances = [];
});

// Anvil2 relies on browser APIs that jsdom lacks.
if (!window.matchMedia) {
  window.matchMedia = (query: string) => ({
    matches: false, media: query, onchange: null,
    addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {},
    dispatchEvent: () => false,
  }) as MediaQueryList;
}
if (!globalThis.ResizeObserver) {
  globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
}

// Anvil2 popovers (select menus) use the Popover API, which jsdom lacks.
for (const m of ["showPopover", "hidePopover", "togglePopover"] as const) {
  if (!(m in HTMLElement.prototype)) (HTMLElement.prototype as unknown as Record<string, () => void>)[m] = () => {};
}
if (!globalThis.IntersectionObserver) {
  globalThis.IntersectionObserver = class { observe() {} unobserve() {} disconnect() {} takeRecords() { return []; } } as unknown as typeof IntersectionObserver;
}
if (!Element.prototype.scrollIntoView) Element.prototype.scrollIntoView = () => {};
