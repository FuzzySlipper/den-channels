import type { AgentStreamEntry } from '../api/types';

interface Props {
  entry: AgentStreamEntry;
  onClose: () => void;
  onOpenTask: (taskId: number, projectId?: string | null) => void;
  onOpenThread: (entry: AgentStreamEntry) => void;
  onOpenDispatch: (dispatchId: number) => void;
}

function formatLabel(value: string): string {
  return value.replace(/_/g, ' ');
}

function getRecipient(entry: AgentStreamEntry): string | null {
  if (entry.recipient_instance_id) return entry.recipient_instance_id;
  if (entry.recipient_agent && entry.recipient_role) return `${entry.recipient_agent} (${entry.recipient_role})`;
  if (entry.recipient_agent) return entry.recipient_agent;
  if (entry.recipient_role) return `role:${entry.recipient_role}`;
  return null;
}

export function AgentStreamDetail({
  entry,
  onClose,
  onOpenTask,
  onOpenThread,
  onOpenDispatch,
}: Props) {
  const metadata = entry.metadata ? JSON.stringify(entry.metadata, null, 2) : null;
  const recipient = getRecipient(entry);

  return (
    <div className="detail-overlay">
      <div className="detail-header">
        <h2>{formatLabel(entry.event_type)}</h2>
        <button className="detail-close" onClick={onClose}>✕</button>
      </div>
      <div className="detail-body">
        <div className="detail-section">
          <dl className="detail-meta">
            <dt>Entry</dt>
            <dd>#{entry.id}</dd>
            <dt>Kind</dt>
            <dd><span className={`stream-chip stream-chip-${entry.stream_kind}`}>{entry.stream_kind}</span></dd>
            <dt>Delivery</dt>
            <dd>{formatLabel(entry.delivery_mode)}</dd>
            <dt>Sender</dt>
            <dd>{entry.sender}</dd>
            {recipient && (
              <>
                <dt>Recipient</dt>
                <dd>{recipient}</dd>
              </>
            )}
            {entry.project_id && (
              <>
                <dt>Project</dt>
                <dd>{entry.project_id}</dd>
              </>
            )}
            <dt>Time</dt>
            <dd>{new Date(entry.created_at + 'Z').toLocaleString()}</dd>
          </dl>
        </div>

        {(entry.task_id != null || entry.thread_id != null || entry.dispatch_id != null) && (
          <div className="detail-section">
            <h3>Links</h3>
            <div className="detail-action-row">
              {entry.task_id != null && (
                <button type="button" className="dispatch-action" onClick={() => onOpenTask(entry.task_id!, entry.project_id)}>
                  Open task #{entry.task_id}
                </button>
              )}
              {entry.thread_id != null && entry.project_id && (
                <button type="button" className="dispatch-action" onClick={() => onOpenThread(entry)}>
                  Open thread #{entry.thread_id}
                </button>
              )}
              {entry.dispatch_id != null && (
                <button type="button" className="dispatch-action" onClick={() => onOpenDispatch(entry.dispatch_id!)}>
                  Open dispatch #{entry.dispatch_id}
                </button>
              )}
            </div>
          </div>
        )}

        {entry.body && (
          <div className="detail-section">
            <h3>Body</h3>
            <div className="detail-description">{entry.body}</div>
          </div>
        )}

        {metadata && (
          <div className="detail-section">
            <h3>Metadata</h3>
            <div className="detail-description">{metadata}</div>
          </div>
        )}
      </div>
    </div>
  );
}
