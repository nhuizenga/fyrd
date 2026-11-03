import { Alert, Button, Chip, Menu, Text } from "@servicetitan/anvil2";
import MoreIcon from "@servicetitan/anvil2/assets/icons/material/round/more_vert.svg?react";
import ReadyIcon from "@servicetitan/anvil2/assets/icons/material/round/check_circle.svg?react";
import MergeIcon from "@servicetitan/anvil2/assets/icons/material/round/call_merge.svg?react";
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
  /** Owned through a stack rather than on one of this session's own branches. */
  stacked?: boolean;
  /** The other hub sessions that own this PR, directly or through a stack. */
  alsoOn?: HubSessionView[];
  onOpenSession?: (id: string) => void;
  /** The nudges that apply to this PR (empty hides the menu), and what to do when one is picked. */
  nudges?: Nudge[];
  onNudge?: (nudge: Nudge) => void;
  /** "Sent to <session>" after a nudge went out. */
  notice?: string | null;
  /** The open PR this one is stacked on, and the open PRs stacked on this one (all in the same list). */
  stackedOn?: PullRequestStatus;
  stackedAbove?: PullRequestStatus[];
}

/** One pull request in the details pane: title line, status badges, actions and its own inline error. */
const jumpTo = (number: number) =>
  document.getElementById(`pr-item-${number}`)?.scrollIntoView?.({ block: "nearest", behavior: "smooth" });

export default function PullRequestItem({ url, status: st, actionsHidden, error, onReady, onMerge, stacked, alsoOn = [], onOpenSession, nudges = [], onNudge, notice, stackedOn, stackedAbove = [] }: Props) {
  const label = prLabel(url);
  const known = st && st.state !== "unknown";
  const title = known ? st.title : "";
  const checksShown = known && st.checks !== "none";
  return (
    <li className="pr-item" id={st?.number ? `pr-item-${st.number}` : undefined}>
      <div className="pr-line">
        <Chip label={label} size="small" title={title ? `${label} ${title}` : url} />
        {stacked && <Chip label="stacked" size="small" title="Owned through a stack with this session's own PRs" />}
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
          {!actionsHidden && nudges.length > 0 && (
            <Menu appearance="secondary" size="small" icon={MoreIcon} label="" aria-label={`Ask for help with ${label}`} title="Ask a session to do something about this PR">
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
            <Chip
              label={`✓ ${st.checksPassed}  ✗ ${st.checksFailed}  ◌ ${st.checksPending}`}
              size="small"
              color={st.checks === "failing" ? DANGER : st.checks === "pending" ? WARNING : undefined}
              title={`Checks ${st.checks}${st.failingCheck ? `: ${st.failingCheck.name}` : ""}`}
              {...(st.failingCheck?.url
                ? { onClick: () => window.open(st.failingCheck!.url!, "_blank", "noopener,noreferrer") }
                : {})}
            />
          )}
          {st.mergeable === "conflicting" && <Chip label="Conflicts" size="small" color={DANGER} />}
          {st.mergeState === "behind" && <Chip label="Behind base" size="small" color={WARNING} />}
          {st.review !== "none" && <Chip label={REVIEW_LABEL[st.review]} size="small" />}
          {st.base && <Chip label={`→ ${st.base}`} size="small" title={`Merges into ${st.base}`} />}
        </span>
      )}
      {st?.state === "unknown" && st.error && <Text size="small" subdued>{st.error}</Text>}
      {(stackedOn || stackedAbove.length > 0) && (
        <span className="pr-stack-links">
          {stackedOn && (
            <button type="button" className="pr-also-on-link" onClick={() => jumpTo(stackedOn.number)}>
              Stacked on #{stackedOn.number}
            </button>
          )}
          {stackedAbove.map((a) => (
            <button key={a.url} type="button" className="pr-also-on-link" onClick={() => jumpTo(a.number)}>
              #{a.number} stacked on this
            </button>
          ))}
        </span>
      )}
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
      {notice && <Text size="small" role="status">{notice}</Text>}
      {error && <Alert status="danger" title={error} role="alert" />}
    </li>
  );
}
