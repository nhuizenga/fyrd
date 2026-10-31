import type { HubState } from "../types";

export interface ScreenProps {
  state: HubState | null;
  onChanged: () => void | Promise<void>;
  /** Switch the console to the Inbox screen. */
  onOpenInbox?: () => void;
}
