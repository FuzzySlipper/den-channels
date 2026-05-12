import type { DispatchEntry } from '../api/types';

interface Props {
  dispatch: DispatchEntry;
  onClose: () => void;
  onOpenTask: (taskId: number, projectId?: string | null) => void;
}

export function DispatchDetail({ dispatch, onClose, onOpenTask }: Props) {
  return (
    <div className="detail-overlay">
      <div className="detail-header">
        <h2>Dispatch #{dispatch.id}</h2>
        <button className="detail-close" onClick={onClose}>✕</button>
      </div>
      <div className="detail-body">
        <div className="detail-section">
          <dl className="detail-meta">
            <dt>Status</dt>
            <dd><span className={`dispatch-status dispatch-status-${dispatch.status}`}>{dispatch.status}</span></dd>
            <dt>Target</dt>
            <dd>{dispatch.target_agent}</dd>
            <dt>Project</dt>
            <dd>{dispatch.project_id}</dd>
            <dt>Trigger</dt>
            <dd>{dispatch.trigger_type} #{dispatch.trigger_id}</dd>
            {dispatch.task_id != null && (
              <>
                <dt>Task</dt>
                <dd>
                  <button type="button" className="stream-link" onClick={() => onOpenTask(dispatch.task_id!, dispatch.project_id)}>
                    #{dispatch.task_id}
                  </button>
                </dd>
              </>
            )}
            <dt>Created</dt>
            <dd>{new Date(dispatch.created_at + 'Z').toLocaleString()}</dd>
            {dispatch.decided_by && (
              <>
                <dt>Decided By</dt>
                <dd>{dispatch.decided_by}</dd>
              </>
            )}
            {dispatch.completed_by && (
              <>
                <dt>Completed By</dt>
                <dd>{dispatch.completed_by}</dd>
              </>
            )}
          </dl>
        </div>

        {dispatch.summary && (
          <div className="detail-section">
            <h3>Summary</h3>
            <div className="detail-description">{dispatch.summary}</div>
          </div>
        )}

        {dispatch.context_prompt && (
          <div className="detail-section">
            <h3>Context Prompt</h3>
            <div className="detail-description">{dispatch.context_prompt}</div>
          </div>
        )}
      </div>
    </div>
  );
}
