import { relativeTime } from "./time";

const now = Date.parse("2026-11-01T12:00:00Z");
const ago = (s: number) => new Date(now - s * 1000).toISOString();

it.each([
  [10, "just now"],
  [60, "1 minute ago"],
  [5 * 60, "5 minutes ago"],
  [3 * 3600, "3 hours ago"],
  [24 * 3600, "1 day ago"],
  [8 * 24 * 3600, "1 week ago"],
])("%i seconds ago is %s", (s, expected) => {
  expect(relativeTime(ago(s), now)).toBe(expected);
});

it.each([null, undefined, "", "not a date"])("%j is a dash", (v) => {
  expect(relativeTime(v, now)).toBe("—");
});
