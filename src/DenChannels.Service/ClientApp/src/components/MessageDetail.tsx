import { useEffect, useState } from 'react';
import type { Message, Thread } from '../api/types';
import { getThread } from '../api/client';
import { formatTimeAgo } from '../utils';
import { messageIntentLabel } from '../messageIntents';

interface Props {
  message: Message;
  onClose: () => void;
}

export function MessageDetail({ message, onClose }: Props) {
  const [thread, setThread] = useState<Thread | null>(null);
  const threadRootId = message.thread_id ?? message.id;

  useEffect(() => {
    let cancelled = false;
    getThread(message.project_id, threadRootId)
      .then(t => { if (!cancelled) setThread(t); })
      .catch(() => { if (!cancelled) setThread(null); });
    return () => { cancelled = true; };
  }, [message.project_id, threadRootId]);

  const displayMessage = thread?.root ?? message;
  const replyCount = thread?.replies.length ?? 0;
  const title = replyCount > 0
    ? `Thread started by ${displayMessage.sender}`
    : `Message from ${displayMessage.sender}`;

  return (
    <div className="detail-overlay">
      <div className="detail-header">
        <h2>{title}</h2>
        <button className="detail-close" onClick={onClose}>✕</button>
      </div>
      <div className="detail-body">
        <div className="detail-section">
          <dl className="detail-meta">
            <dt>From</dt>
            <dd>{displayMessage.sender}</dd>
            <dt>Intent</dt>
            <dd>
              <span className={`intent-chip intent-${displayMessage.intent}`}>
                {messageIntentLabel(displayMessage.intent)}
              </span>
            </dd>
            <dt>Project</dt>
            <dd>{displayMessage.project_id}</dd>
            <dt>Time</dt>
            <dd>{new Date(displayMessage.created_at + 'Z').toLocaleString()}</dd>
            {displayMessage.task_id != null && (
              <><dt>Task</dt><dd>#{displayMessage.task_id}</dd></>
            )}
            {replyCount > 0 && (
              <><dt>Replies</dt><dd>{replyCount}</dd></>
            )}
            {message.thread_id != null && (
              <><dt>Opened from</dt><dd>reply #{message.id}</dd></>
            )}
          </dl>
        </div>

        <div className="detail-section">
          <h3>Content</h3>
          <div className="detail-description">{displayMessage.content}</div>
        </div>

        {thread && thread.replies.length > 0 && (
          <div className="detail-section">
            <h3>Replies</h3>
            {thread.replies.map(reply => (
              <div key={reply.id} style={{ marginBottom: 12 }}>
                <div style={{ display: 'flex', gap: 6, alignItems: 'baseline', marginBottom: 2 }}>
                  <span className="message-time">{formatTimeAgo(reply.created_at)}</span>
                  <span className={`intent-chip intent-${reply.intent}`}>{messageIntentLabel(reply.intent)}</span>
                  <span className="message-sender">{reply.sender}:</span>
                </div>
                <div className="detail-description" style={{ paddingLeft: 10 }}>
                  {reply.content}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
