import { useCallback, useEffect, useState } from "react";
import { Alert, AnvilProvider, Badge, Button, Flex, Text } from "@servicetitan/anvil2";
import * as api from "./api";
import { navigate, useRoute, type Screen } from "./route";
import InboxScreen from "./screens/InboxScreen";
import SessionsScreen from "./screens/SessionsScreen";
import SettingsScreen from "./screens/SettingsScreen";
import type { HubState } from "./types";

const POLL_MS = 3000;

export default function App() {
  const { screen, id: routeId } = useRoute();
  const [state, setState] = useState<HubState | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dismissedError, setDismissedError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      setState(await api.getHubState());
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    void refresh();
    const tick = () => {
      if (document.visibilityState === "visible") void refresh();
    };
    const timer = setInterval(tick, POLL_MS);
    document.addEventListener("visibilitychange", tick);
    return () => {
      clearInterval(timer);
      document.removeEventListener("visibilitychange", tick);
    };
  }, [refresh]);

  // The server defines "today" in this zone, so keep it in step with the browser's. Once, after the first config load.
  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
        const c = await api.getConfig();
        if (live && tz && c && c.timeZone !== tz) await api.updateConfig({ timeZone: tz });
      } catch {
        /* spend limits keep using the previous zone */
      }
    })();
    return () => { live = false; };
  }, []);

  const questions = state?.questions ?? [];
  const lastError = state?.lastError && state.lastError !== dismissedError ? state.lastError : null;

  const navButton = (id: Screen, label: string, count?: number) => (
    <Button
      appearance={screen === id ? "secondary" : "ghost"}
      aria-current={screen === id ? "page" : undefined}
      aria-label={count ? `${label} ${count}` : label}
      onClick={() => navigate(id)}
    >
      {label}
      {count ? <Badge>{count}</Badge> : null}
    </Button>
  );

  return (
    <AnvilProvider>
      <div className="shell">
        <header className="shell-header">
          <div className="brand"><img src="/fyrd-icon-32.png" alt="" width={24} height={24} /><Text variant="headline" el="h1" size="medium">fyrd</Text></div>
          <nav aria-label="Primary">
            <Flex gap="2">
              {navButton("inbox", "Inbox", questions.length)}
              {navButton("sessions", "Sessions")}
              {navButton("settings", "Settings")}
            </Flex>
          </nav>
        </header>
        <main className={screen === "sessions" ? "shell-fill" : "shell-scroll"}>
          <div className={screen === "sessions" ? "sessions" : "shell-main"}>
          {error && (
            <Alert status="danger" title={`Can't reach the Fyrd server: ${error}`} role="alert" />
          )}
          {state?.signedOut && (
            <Alert status="danger" title="Signed out of Claude" role="alert">
              Run <code>claude auth login</code>
            </Alert>
          )}
          {lastError && (
            <Alert status="warning" title="Poll problem" role="alert" onClose={() => setDismissedError(lastError)}>
              {lastError}
            </Alert>
          )}
          {screen === "inbox" ? (
            <InboxScreen state={state} onChanged={refresh} routeId={routeId} />
          ) : screen === "sessions" ? (
            <SessionsScreen state={state} onChanged={refresh} routeId={routeId} />
          ) : (
            <SettingsScreen state={state} onChanged={refresh} routeId={routeId} />
          )}
          </div>
        </main>
      </div>
    </AnvilProvider>
  );
}
