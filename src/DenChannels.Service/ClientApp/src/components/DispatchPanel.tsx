import type { DispatchEntry } from '../api/types';
import { formatTimeAgo, truncate } from '../utils';

// Legacy/debug component retained for a possible explicit dispatch view.
// The default dashboard no longer renders dispatch approval/reject controls.
interface Props {
  dispatches: DispatchEntry[];
  isGlobal: boolean;
  pendingActionId: number | null;
  actionError: string | null;
  onApprove: (dispatch: DispatchEntry) => void;
  onReject: (dispatch: DispatchEntry) => void;
}

export function DispatchPanel({
  dispatches,
  isGlobal,
  pendingActionId,
  actionError,
  onApprove,
  onReject,
}: Props) {
  if (dispatches.length === 0) {
    return (
      <div className="dispatch-empty-state">
        <div className="empty">No pending dispatches.</div>
        {actionError && <div className="dispatch-error-banner">{actionError}</div>}
      </div>
    );
  }

  return (
    <div className="dispatch-list">
      {actionError && <div className="dispatch-error-banner">{actionError}</div>}
      {dispatches.map(dispatch => {
        const summary = (dispatch.summary ?? `${dispatch.trigger_type} #${dispatch.trigger_id}`).replace(/\s+/g, ' ').trim();
        const isBusy = pendingActionId === dispatch.id;

        return (
          <div key={dispatch.id} className="dispatch-item">
            <div className="dispatch-content">
              <div className="dispatch-topline">
                <span className={`dispatch-status dispatch-status-${dispatch.status}`}>
                  {dispatch.status}
                </span>
                <span className="dispatch-time">{formatTimeAgo(dispatch.created_at)}</span>
                {isGlobal && <span className="dispatch-project">@{truncate(dispatch.project_id, 10)}</span>}
                <span className="dispatch-target">{dispatch.target_agent}</span>
                {dispatch.task_id != null && <span className="dispatch-task">#{dispatch.task_id}</span>}
              </div>
              <div className="dispatch-summary" title={summary}>
                {truncate(summary, isGlobal ? 78 : 96)}
              </div>
            </div>
            <div className="dispatch-actions">
              <button
                type="button"
                className="dispatch-action dispatch-action-approve"
                disabled={isBusy}
                onClick={() => onApprove(dispatch)}
              >
                {isBusy ? 'Working...' : 'Approve'}
              </button>
              <button
                type="button"
                className="dispatch-action dispatch-action-reject"
                disabled={isBusy}
                onClick={() => onReject(dispatch)}
              >
                Reject
              </button>
            </div>
          </div>
        );
      })}
    </div>
  );
}
