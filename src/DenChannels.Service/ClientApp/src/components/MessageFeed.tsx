import type { Message, MessageFeedItem } from '../api/types';
import { formatTimeAgo, truncate } from '../utils';
import { messageIntentLabel } from '../messageIntents';

interface Props {
  messages: MessageFeedItem[];
  isGlobal: boolean;
  onSelect: (message: Message) => void;
}

export function MessageFeed({ messages, isGlobal, onSelect }: Props) {
  if (messages.length === 0) {
    return <div className="empty">No messages</div>;
  }

  return (
    <>
      {messages.map(item => {
        const root = item.root_message;
        const latest = item.latest_message;
        const isThread = item.reply_count > 0;

        return (
          <div
            key={root.id}
            className={`message-feed-item${isThread ? ' message-feed-item-thread' : ''}`}
            onClick={() => onSelect(root)}
          >
            <div className="message-feed-topline">
              <span className="message-time">{formatTimeAgo(item.latest_activity_at)}</span>
              {isGlobal && <span className="message-project-tag">[{truncate(root.project_id, 12)}]</span>}
              <span className={`intent-chip intent-${latest.intent}`}>{messageIntentLabel(latest.intent)}</span>
              {isThread ? (
                <>
                  <span className="message-thread-badge">Thread</span>
                  <span className="message-thread-meta">
                    {item.reply_count} {item.reply_count === 1 ? 'reply' : 'replies'}
                  </span>
                </>
              ) : (
                <span className="message-sender">{latest.sender}:</span>
              )}
            </div>

            <div className="message-feed-bottomline">
              {isThread ? (
                <>
                  <span className="message-sender">{root.sender}</span>
                  <span className="message-thread-meta">started the thread</span>
                  {latest.id !== root.id && (
                    <>
                      <span className="message-thread-divider">·</span>
                      <span className="message-thread-meta">latest</span>
                      <span className="message-sender">{latest.sender}</span>
                    </>
                  )}
                  <span className="message-content">
                    {truncate(latest.content.replace(/\n/g, ' '), 80)}
                  </span>
                </>
              ) : (
                <span className="message-content">
                  {truncate(latest.content.replace(/\n/g, ' '), 80)}
                </span>
              )}
            </div>
          </div>
        );
      })}
    </>
  );
}
