import { useEffect, useState } from "react";

export type Screen = "inbox" | "sessions" | "settings";

/** `#/inbox`, `#/sessions`, `#/sessions/{sessionId}`, `#/settings`, `#/settings/{section}`. */
export interface Route {
  screen: Screen;
  /** The session id on Sessions, or the section id on Settings. */
  id: string | null;
}

const SCREENS: Screen[] = ["inbox", "sessions", "settings"];

export function parseRoute(hash: string): Route {
  const [screen, id] = hash.replace(/^#\/?/, "").split("/").map((p) => decodeURIComponent(p));
  return SCREENS.includes(screen as Screen)
    ? { screen: screen as Screen, id: id || null }
    : { screen: "inbox", id: null };
}

export const routeHref = (screen: Screen, id?: string | null) =>
  `#/${screen}${id ? `/${encodeURIComponent(id)}` : ""}`;

export function navigate(screen: Screen, id?: string | null) {
  window.location.hash = routeHref(screen, id);
}

/** The current hash route; follows back/forward and links. */
export function useRoute(): Route {
  const [route, setRoute] = useState(() => parseRoute(window.location.hash));
  useEffect(() => {
    const onChange = () => setRoute(parseRoute(window.location.hash));
    window.addEventListener("hashchange", onChange);
    return () => window.removeEventListener("hashchange", onChange);
  }, []);
  return route;
}

/** Swaps the current history entry, so redirects (like restoring the last session) don't break Back. */
export function replaceRoute(screen: Screen, id?: string | null) {
  window.history.replaceState(null, "", routeHref(screen, id));
  window.dispatchEvent(new HashChangeEvent("hashchange"));
}

const LAST_SESSION = "fyrd.lastSession";

/** The most recently viewed session, kept per browser so `#/sessions` can reopen it. */
export function lastSession(): string | null {
  try {
    return window.localStorage.getItem(LAST_SESSION);
  } catch {
    return null;
  }
}

export function rememberSession(id: string) {
  try {
    window.localStorage.setItem(LAST_SESSION, id);
  } catch {
    /* storage blocked: the Sessions screen just opens with nothing selected */
  }
}
