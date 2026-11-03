import CheckIcon from "@servicetitan/anvil2/assets/icons/material/round/check_circle.svg?react";
import CancelIcon from "@servicetitan/anvil2/assets/icons/material/round/cancel.svg?react";
import PendingIcon from "@servicetitan/anvil2/assets/icons/material/round/pending.svg?react";
import WarnIcon from "@servicetitan/anvil2/assets/icons/material/round/error.svg?react";
import CircleIcon from "@servicetitan/anvil2/assets/icons/material/round/radio_button_unchecked.svg?react";
import { HEALTH_LABEL, worstHealth } from "../prStatus";
import type { PullRequestStatus } from "../types";

type Look = { cls: string; label: string; Icon: typeof CheckIcon };

/** The one status icon for a session's PRs: the worst open PR's health, else merged / closed, else not known yet. */
export function prLook(statuses: (PullRequestStatus | undefined)[]): Look {
  const open = statuses.filter((s) => s?.state === "open");
  if (open.length > 0) {
    const health = worstHealth(open);
    switch (health) {
      case "failing": return { cls: "fail", label: HEALTH_LABEL.failing, Icon: CancelIcon };
      case "conflicts": return { cls: "fail", label: HEALTH_LABEL.conflicts, Icon: WarnIcon };
      case "pending": return { cls: "pending", label: HEALTH_LABEL.pending, Icon: PendingIcon };
      case "passing": return { cls: "pass", label: HEALTH_LABEL.passing, Icon: CheckIcon };
      default: return { cls: "none", label: "Checks not run", Icon: CircleIcon };
    }
  }
  if (statuses.length > 0 && statuses.every((s) => s?.state === "merged")) return { cls: "merged", label: "Merged", Icon: CheckIcon };
  if (statuses.length > 0 && statuses.every((s) => s?.state === "merged" || s?.state === "closed")) return { cls: "none", label: "Closed", Icon: CancelIcon };
  return { cls: "none", label: "Status unknown", Icon: CircleIcon };
}

/** A coloured icon for the state of a session's PRs (green passing, red failing, amber running, grey otherwise). */
export default function PrStatusIcon({ statuses }: { statuses: (PullRequestStatus | undefined)[] }) {
  const { cls, label, Icon } = prLook(statuses);
  return (
    <span className={`pr-status-icon pr-status-${cls}`} role="img" aria-label={label} title={label}>
      <Icon width={14} height={14} />
    </span>
  );
}
