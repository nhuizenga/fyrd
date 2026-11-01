import { parseRoute, routeHref } from "./route";

it.each([
  ["", { screen: "inbox", id: null }],
  ["#/inbox", { screen: "inbox", id: null }],
  ["#/sessions", { screen: "sessions", id: null }],
  ["#/sessions/session_01ABC", { screen: "sessions", id: "session_01ABC" }],
  ["#/settings/personas", { screen: "settings", id: "personas" }],
  ["#/nowhere/x", { screen: "inbox", id: null }],
])("parses %j", (hash, route) => {
  expect(parseRoute(hash)).toEqual(route);
});

it("round-trips a session link", () => {
  expect(parseRoute(routeHref("sessions", "session_01ABC"))).toEqual({ screen: "sessions", id: "session_01ABC" });
});
