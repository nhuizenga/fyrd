import type { HubState } from "../types";

export interface ScreenProps {
  state: HubState | null;
  onChanged: () => void | Promise<void>;
  /** The id from the hash route: the session on Sessions (`#/sessions/{id}`), the section on Settings. */
  routeId?: string | null;
}
