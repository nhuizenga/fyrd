import { Alert, Button, Chip, Menu, Text } from "@servicetitan/anvil2";
import MoreIcon from "@servicetitan/anvil2/assets/icons/material/round/more_vert.svg?react";
import ReadyIcon from "@servicetitan/anvil2/assets/icons/material/round/check_circle.svg?react";
import MergeIcon from "@servicetitan/anvil2/assets/icons/material/round/call_merge.svg?react";
import CheckIcon from "@servicetitan/anvil2/assets/icons/material/round/check_circle.svg?react";
import CancelIcon from "@servicetitan/anvil2/assets/icons/material/round/cancel.svg?react";
import PendingIcon from "@servicetitan/anvil2/assets/icons/material/round/pending.svg?react";
import OpenIcon from "@servicetitan/anvil2/assets/icons/material/round/open_in_new.svg?react";
import type { Nudge } from "../prNudges";
import type { HubSessionView, PullRequestStatus } from "../types";

export const prLabel = (url: string) => (/\/pull\/(\d+)/.exec(url)?.[1] ? `#${/\/pull\/(\d+)/.exec(url)![1]}` : url);

const DANGER = "#fde8e8";
const WARNING = "#fff4d6";

const REVIEW_LABEL = { commented: "Reviewed", approved: "Approved", changes_requested: "Changes requested" } as const;

interface Props {
  url: string;
  /** Null until GitHub has been read. */
  status: PullRequestStatus | undefined;
  /** No GitHub access: nothing to act on. */
  actionsHidden: boolean;
  /** Why the last action on this PR failed. */
  error?: string | null;
  onReady: () => void;
  onMerge: () => void;
  /** The other hub sessions that own this PR, directly or through a stack. */
  alsoOn?: HubSessionView[];
  onOpenSession?: (id: string) => void;
  /** The nudges that apply to this PR (empty hides the menu), and what to do when one is picked. */
  nudges?: Nudge[];
  onNudge?: (nudge: Nudge) => void;
  /** "Sent to <session>" after a nudge went out. */
  notice?: string | null;
  /** Offer "Merge stack" (the PR is in a linear chain of two or more open PRs and no job is running on it). */
  canMergeStack?: boolean;
  onMergeStack?: () => void;
  /** The stack-merge step line for this PR, and Cancel while this PR is the step being worked on. */
  jobLine?: string | null;
  jobFailed?: boolean;
  onCancelJob?: () => void;
}

/** One pull request in the details pane: title line, status badges, actions and its own inline error. */

export default function PullRequestItem({ url, status: st, actionsHidden, error, onReady, onMerge, alsoOn = [], onOpenSession, nudges = [], onNudge, notice, canMergeStack, onMergeStack, jobLine, jobFailed, onCancelJob }: Props) {
  const label = prLabel(url);
  const known = st && st.state !== "unknown";
  const title = known ? st.title : "";
  const checksShown = known && st.checks !== "none";
  return (
    <li className="pr-item" id={st?.number ? `pr-item-${st.number}` : undefined}>
      <div className="pr-line">
        <Chip label={label} size="small" title={title ? `${label} ${title}` : url} />
        {title && <span className="pr-title" title={title}>{title}</span>}
        <span className="pr-actions">
          <Button
            appearance="secondary" size="small" icon={OpenIcon}
            aria-label={`Open ${label}`} title="Open pull request on GitHub"
            onClick={() => window.open(url, "_blank", "noopener,noreferrer")}
          />
          {!actionsHidden && st?.state === "open" && st.draft && (
            <Button
              appearance="secondary" size="small" icon={ReadyIcon}
              aria-label={`Mark ${label} ready for review`} title="Mark ready for review"
              className="pr-ready" onClick={onReady}
            />
          )}
          {!actionsHidden && (nudges.length > 0 || canMergeStack) && (
            <Menu appearance="secondary" size="small" icon={MoreIcon} label="" aria-label={`Ask for help with ${label}`} title="Ask a session to do something about this PR">
              {canMergeStack && <Menu.Item label="Merge stack" onClick={onMergeStack} title="Squash-merge the whole stack, top down" />}
              {nudges.map((n) => (
                <Menu.Item
                  key={n.kind} label={n.label} disabled={!!n.disabledReason}
                  title={n.disabledReason ?? `Sends to ${n.to?.title ?? "the Controller"}`}
                  onClick={() => onNudge?.(n)}
                />
              ))}
            </Menu>
          )}
          {!actionsHidden && st?.canMerge && (
            <Button
              appearance="secondary" size="small" icon={MergeIcon}
              aria-label={`Merge ${label}`} title="Merge pull request"
              className="pr-merge" onClick={onMerge}
            />
          )}
        </span>
      </div>
      {known && (
        <span className="pr-badges">
          {st.state !== "open" && <Chip label={st.state} size="small" />}
          {st.draft && <Chip label="Draft" size="small" />}
          {checksShown && (
            <span className="pr-checks" role="group" aria-label={`Checks ${st.checks}`}>
              {([
                ["pass", st.checksPassed, CheckIcon, "passed"],
                ["fail", st.checksFailed, CancelIcon, "failed"],
                ["pending", st.checksPending, PendingIcon, "pending"],
              ] as const).map(([kind, n, Icon, word]) => (
                <Chip
                  key={kind}
                  icon={Icon}
                  label={String(n)}
                  size="small"
                  className={`chip-check chip-check-${n > 0 ? kind : "none"}`}
                  color={kind === "fail" && n > 0 ? DANGER : kind === "pending" && n > 0 ? WARNING : undefined}
                  aria-label={`${n} ${word}`}
                  title={`${n} check${n === 1 ? "" : "s"} ${word}${kind === "fail" && st.failingCheck ? `: ${st.failingCheck.name}` : ""}`}
                  {...(kind === "fail" && st.failingCheck?.url
                    ? { onClick: () => window.open(st.failingCheck!.url!, "_blank", "noopener,noreferrer") }
                    : {})}
                />
              ))}
            </span>
          )}
          {st.mergeable === "conflicting" && <Chip label="Conflicts" size="small" color={DANGER} />}
          {st.mergeState === "behind" && <Chip label="Behind base" size="small" color={WARNING} />}
          {st.review !== "none" && <Chip label={REVIEW_LABEL[st.review]} size="small" />}
          {st.base && <Chip label={`→ ${st.base}`} size="small" title={`Merges into ${st.base}`} />}
        </span>
      )}
      {st?.state === "unknown" && st.error && <Text size="small" subdued>{st.error}</Text>}
      {alsoOn.length > 0 && (
        <span className="pr-also-on">
          <Text size="small" subdued>Also on:</Text>
          {alsoOn.map((o) => (
            <button
              key={o.id} type="button" className="pr-also-on-link"
              aria-label={`Open ${o.title}`} onClick={() => onOpenSession?.(o.id)}
            >
              {o.title} · {o.status}
            </button>
          ))}
        </span>
      )}
      {jobLine && (
        <span className="pr-job-line">
          <Text size="small" role="status" className={jobFailed ? "pr-job-failed" : undefined}>{jobLine}</Text>
          {onCancelJob && <Button appearance="secondary" size="small" onClick={onCancelJob}>Cancel stack merge</Button>}
        </span>
      )}
      {notice && <Text size="small" role="status">{notice}</Text>}
      {error && <Alert status="danger" title={error} role="alert" />}
    </li>
  );
}
