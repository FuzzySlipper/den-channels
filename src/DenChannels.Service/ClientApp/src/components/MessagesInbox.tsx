import { useCallback, useMemo, useState } from 'react';
import type { Message, Space } from '../api/types';
import { getMessages } from '../api/client';
import { usePolling } from '../hooks/usePolling';
import { formatTimeAgo } from '../utils';
import { messageIntentLabel } from '../messageIntents';

interface Props {
  spaces: Space[];
  currentSpaceId: string | null;
  isAggregate: boolean;
  onSelect: (message: Message) => void;
  onOpenTask: (taskId: number, projectId?: string | null) => void;
}

interface MessageRow extends Message {
  userDirected: boolean;
  urgency: string | null;
  metadataType: string | null;
}

function metadataObject(message: Message): Record<string, unknown> {
  return message.metadata && typeof message.metadata === 'object' && !Array.isArray(message.metadata)
    ? message.metadata as Record<string, unknown>
    : {};
}

function firstString(...values: unknown[]): string | null {
  for (const value of values) {
    if (typeof value === 'string' && value.trim().length > 0) return value.trim();
  }
  return null;
}

function isUserDirected(message: Message): boolean {
  const metadata = metadataObject(message);
  const recipient = firstString(metadata.recipient, metadata.recipient_agent, metadata.target, metadata.to, metadata.audience);
  const type = firstString(metadata.type, metadata.kind);
  return message.intent === 'question'
    || message.intent === 'task_blocked'
    || message.intent === 'notification'
    || type === 'user_notification'
    || type === 'notification'
    || recipient === 'user'
    || recipient === 'Patchfoot'
    || recipient === 'Patch';
}

function toRow(message: Message): MessageRow {
  const metadata = metadataObject(message);
  return {
    ...message,
    userDirected: isUserDirected(message),
    urgency: firstString(metadata.urgency, metadata.priority),
    metadataType: firstString(metadata.type, metadata.kind),
  };
}

function preview(content: string): string {
  const singleLine = content.replace(/\s+/g, ' ').trim();
  return singleLine.length > 180 ? `${singleLine.slice(0, 179)}…` : singleLine;
}

function messageScopeLabel(message: Message): string {
  return message.task_id != null ? `${message.project_id} · task #${message.task_id}` : `${message.project_id} · project`;
}

export function MessagesInbox({ spaces, currentSpaceId, isAggregate, onSelect, onOpenTask }: Props) {
  const [senderFilter, setSenderFilter] = useState('');
  const [taskFilter, setTaskFilter] = useState('');
  const [messageScopeFilter, setMessageScopeFilter] = useState<'all' | 'project' | 'task' | 'user'>('all');

  const projectIds = useMemo(() => {
    if (!isAggregate && currentSpaceId) return [currentSpaceId];
    return spaces
      .filter(space => space.id !== '_all' && (space.kind === 'project' || space.id === '_global'))
      .map(space => space.id);
  }, [currentSpaceId, isAggregate, spaces]);

  const parsedTaskId = useMemo(() => {
    const trimmed = taskFilter.trim().replace(/^#/, '');
    return /^\d+$/.test(trimmed) ? Number(trimmed) : undefined;
  }, [taskFilter]);

  const fetchMessages = useCallback(async () => {
    const pages = await Promise.all(projectIds.map(async projectId => {
      try {
        return await getMessages(projectId, { taskId: parsedTaskId, limit: 80 });
      } catch (error) {
        console.warn('Failed to load messages for project', projectId, error);
        return [];
      }
    }));
    return pages.flat().map(toRow).sort((a, b) => b.created_at.localeCompare(a.created_at));
  }, [parsedTaskId, projectIds]);

  const { data, loading, error, refresh } = usePolling<MessageRow[]>(fetchMessages, 5000);
  const senderOptions = useMemo(() => Array.from(new Set((data ?? []).map(message => message.sender))).sort(), [data]);
  const filteredMessages = useMemo(() => {
    const sender = senderFilter.trim().toLowerCase();
    return (data ?? []).filter(message => {
      if (sender && !message.sender.toLowerCase().includes(sender)) return false;
      if (messageScopeFilter === 'project' && message.task_id != null) return false;
      if (messageScopeFilter === 'task' && message.task_id == null) return false;
      if (messageScopeFilter === 'user' && !message.userDirected) return false;
      return true;
    });
  }, [data, messageScopeFilter, senderFilter]);

  return (
    <div className="messages-inbox" aria-label="Den messages inbox">
      <div className="messages-inbox-toolbar">
        <label>
          <span>Scope</span>
          <select value={messageScopeFilter} onChange={event => setMessageScopeFilter(event.target.value as typeof messageScopeFilter)}>
            <option value="all">All messages</option>
            <option value="project">Project-level</option>
            <option value="task">Task-attached</option>
            <option value="user">User-directed</option>
          </select>
        </label>
        <label>
          <span>Sender</span>
          <input
            list="messages-inbox-senders"
            value={senderFilter}
            onChange={event => setSenderFilter(event.target.value)}
            placeholder="sender"
          />
          <datalist id="messages-inbox-senders">
            {senderOptions.map(sender => <option key={sender} value={sender} />)}
          </datalist>
        </label>
        <label>
          <span>Task</span>
          <input value={taskFilter} onChange={event => setTaskFilter(event.target.value)} placeholder="#1548" />
        </label>
        <button type="button" onClick={refresh}>Refresh</button>
      </div>

      <div className="messages-inbox-note">
        Den project/task messages and user notifications are separate from channel chat and agent activity breadcrumbs.
        {isAggregate ? ' Aggregate view loads recent messages from visible project spaces.' : ' Project view includes project-level and task-attached messages.'}
      </div>

      {error ? (
        <div className="messages-inbox-state messages-inbox-error">{error.message}</div>
      ) : loading && filteredMessages.length === 0 ? (
        <div className="messages-inbox-state">Loading messages…</div>
      ) : filteredMessages.length === 0 ? (
        <div className="messages-inbox-state messages-inbox-muted">No matching Den messages yet.</div>
      ) : (
        <div className="messages-inbox-list">
          {filteredMessages.map(message => (
            <article key={`${message.project_id}:${message.id}`} className={`messages-inbox-row ${message.userDirected ? 'messages-inbox-row-user-directed' : ''} ${message.urgency === 'high' ? 'messages-inbox-row-high' : ''}`}>
              <button type="button" className="messages-inbox-main" onClick={() => onSelect(message)}>
                <span className="message-time">{formatTimeAgo(message.created_at)}</span>
                <span className="messages-inbox-sender">{message.sender}</span>
                <span className="messages-inbox-preview">{preview(message.content)}</span>
              </button>
              <div className="messages-inbox-meta">
                <span className={`intent-chip intent-${message.intent}`}>{messageIntentLabel(message.intent)}</span>
                <span>{messageScopeLabel(message)}</span>
                {message.userDirected && <span className="messages-inbox-user-chip">user-directed</span>}
                {message.urgency && <span className={`messages-inbox-urgency messages-inbox-urgency-${message.urgency}`}>{message.urgency}</span>}
                {message.metadataType && <span>{message.metadataType}</span>}
                {message.task_id != null && (
                  <button type="button" className="messages-inbox-task-link" onClick={() => onOpenTask(message.task_id!, message.project_id)}>
                    Open task #{message.task_id}
                  </button>
                )}
              </div>
            </article>
          ))}
        </div>
      )}
    </div>
  );
}
