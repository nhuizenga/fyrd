import { Icon } from "@servicetitan/anvil2";
import Archived from "@servicetitan/anvil2/assets/icons/material/round/archive.svg?react";
import Idle from "@servicetitan/anvil2/assets/icons/material/round/check_circle_outline.svg?react";
import Unknown from "@servicetitan/anvil2/assets/icons/material/round/circle.svg?react";
import Waiting from "@servicetitan/anvil2/assets/icons/material/round/help.svg?react";
import Running from "@servicetitan/anvil2/assets/icons/material/round/sync.svg?react";

const STATUS: Record<string, { svg: typeof Running; color: string; label: string }> = {
  running: { svg: Running, color: "var(--a2-foreground-color-primary)", label: "Running" },
  requires_action: { svg: Waiting, color: "var(--a2-border-color-warning)", label: "Waiting for you" },
  idle: { svg: Idle, color: "var(--a2-border-color-success)", label: "Idle" },
  archived: { svg: Archived, color: "var(--a2-foreground-color-subdued)", label: "Archived" },
};

/** A session status as an icon; the status name is its accessible label and tooltip. */
export default function StatusIcon({ status }: { status: string }) {
  const s = STATUS[status] ?? { svg: Unknown, color: "var(--a2-foreground-color-subdued)", label: status || "Unknown" };
  return (
    <span className="status-icon" role="img" aria-label={s.label} title={s.label}>
      <Icon svg={s.svg} color={s.color} size="small" />
    </span>
  );
}
