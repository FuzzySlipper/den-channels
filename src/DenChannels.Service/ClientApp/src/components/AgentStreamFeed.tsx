import type { AgentStreamEntry } from '../api/types';
import { formatTimeAgo, truncate } from '../utils';

interface Props {
  entries: AgentStreamEntry[];
  isGlobal: boolean;
  onSelect: (entry: AgentStreamEntry) => void;
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

function getSummary(entry: AgentStreamEntry): string {
  const body = entry.body?.replace(/\s+/g, ' ').trim();
  if (body) return body;
  if (entry.dispatch_id != null) return `Dispatch #${entry.dispatch_id}`;
  if (entry.task_id != null) return `Task #${entry.task_id}`;
  return formatLabel(entry.event_type);
}

export function AgentStreamFeed({
  entries,
  isGlobal,
  onSelect,
  onOpenTask,
  onOpenThread,
  onOpenDispatch,
}: Props) {
  if (entries.length === 0) {
    return <div className="empty">No agent stream entries.</div>;
  }

  return (
    <div className="stream-list">
      {entries.map(entry => {
        const recipient = getRecipient(entry);
        const summary = getSummary(entry);

        return (
          <div
            key={entry.id}
            className="stream-item"
            onClick={() => onSelect(entry)}
          >
            <div className="stream-topline">
              <span className="message-time">{formatTimeAgo(entry.created_at)}</span>
              {isGlobal && entry.project_id && (
                <span className="message-project-tag">[{truncate(entry.project_id, 14)}]</span>
              )}
              <span className={`stream-chip stream-chip-${entry.stream_kind}`}>{entry.stream_kind}</span>
              <span className="stream-chip stream-chip-event">{formatLabel(entry.event_type)}</span>
              <span className="stream-sender">{entry.sender}</span>
              {recipient && (
                <>
                  <span className="stream-arrow">→</span>
                  <span className="stream-recipient">{recipient}</span>
                </>
              )}
            </div>

            <div className="stream-summary">
              {truncate(summary, isGlobal ? 128 : 144)}
            </div>

            {(entry.task_id != null || entry.thread_id != null || entry.dispatch_id != null) && (
              <div className="stream-links">
                {entry.task_id != null && (
                  <button
                    type="button"
                    className="stream-link"
                    onClick={event => {
                      event.stopPropagation();
                      onOpenTask(entry.task_id!, entry.project_id);
                    }}
                  >
                    Task #{entry.task_id}
                  </button>
                )}
                {entry.thread_id != null && entry.project_id && (
                  <button
                    type="button"
                    className="stream-link"
                    onClick={event => {
                      event.stopPropagation();
                      onOpenThread(entry);
                    }}
                  >
                    Thread #{entry.thread_id}
                  </button>
                )}
                {entry.dispatch_id != null && (
                  <button
                    type="button"
                    className="stream-link"
                    onClick={event => {
                      event.stopPropagation();
                      onOpenDispatch(entry.dispatch_id!);
                    }}
                  >
                    Dispatch #{entry.dispatch_id}
                  </button>
                )}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}
