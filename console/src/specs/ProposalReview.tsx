import { useEffect, useState } from "react";
import * as api from "../api";
import type { Proposal } from "../types";

interface Props {
  specId: string;
  onClose: () => void;
  onDone: () => void;
}

const msg = (e: unknown) => (e instanceof Error ? e.message : String(e));

export default function ProposalReview({ specId, onClose, onDone }: Props) {
  const [current, setCurrent] = useState<string | null>(null);
  const [proposal, setProposal] = useState<Proposal | null>(null);
  const [edited, setEdited] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    Promise.all([api.getSpec(specId), api.getProposal(specId)])
      .then(([spec, prop]) => {
        if (!live) return;
        setCurrent(spec.markdown);
        setProposal(prop);
      })
      .catch((e) => live && setError(msg(e)));
    return () => {
      live = false;
    };
  }, [specId]);

  async function act(fn: () => Promise<unknown>) {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await fn();
      onDone();
    } catch (e) {
      setError(msg(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="panel proposal-review" aria-label="Research proposal">
      <header className="panel-head">
        <h3>Research proposal</h3>
        <button type="button" onClick={onClose}>Close</button>
      </header>
      {error && <p className="error" role="alert">{error}</p>}
      {proposal && current !== null && (
        <>
          <p className="proposal-summary">{proposal.summary}</p>
          {proposal.openQuestions.length > 0 && (
            <>
              <h4>Open questions</h4>
              <ul className="open-questions">
                {proposal.openQuestions.map((q, i) => <li key={i}>{q}</li>)}
              </ul>
            </>
          )}
          <div className="review-panes">
            <div>
              <h4>Current spec</h4>
              <pre className="pane">{current}</pre>
            </div>
            <div>
              <h4>Proposal</h4>
              {edited === null ? (
                <pre className="pane">{proposal.markdown}</pre>
              ) : (
                <textarea
                  className="pane"
                  aria-label="Edited proposal"
                  value={edited}
                  onChange={(e) => setEdited(e.target.value)}
                />
              )}
            </div>
          </div>
          <div className="row">
            {edited === null ? (
              <>
                <button type="button" disabled={busy} onClick={() => act(() => api.acceptProposal(specId, undefined))}>
                  Accept
                </button>
                <button type="button" disabled={busy} onClick={() => setEdited(proposal.markdown)}>
                  Edit then accept
                </button>
              </>
            ) : (
              <>
                <button type="button" disabled={busy || !edited.trim()} onClick={() => act(() => api.acceptProposal(specId, edited))}>
                  Accept edited
                </button>
                <button type="button" disabled={busy} onClick={() => setEdited(null)}>Cancel edit</button>
              </>
            )}
            <button type="button" disabled={busy} onClick={() => act(() => api.discardProposal(specId))}>
              Discard
            </button>
          </div>
        </>
      )}
    </section>
  );
}
