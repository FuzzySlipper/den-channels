import type { AgentStreamEntry, SubagentRunSummary } from '../api/types';
import type { ThoughtFeedItem } from '../thoughts';
import { summarizeThoughtItem, thoughtKindLabel, thoughtSourceLabel } from '../thoughts';
import { formatTimeAgo, truncate } from '../utils';

interface Props {
  items: ThoughtFeedItem[];
  isGlobal: boolean;
  loading: boolean;
  error: Error | null;
  showRawReasoning: boolean;
  rawReasoningAvailable: boolean;
  onOpenTask: (taskId: number, projectId?: string | null) => void;
  onOpenRun: (run: SubagentRunSummary) => void;
  onOpenStream: (entry: AgentStreamEntry) => void;
}

function itemTitle(item: ThoughtFeedItem): string {
  if (item.run) return `Open sub-agent run ${item.run.run_id}`;
  if (item.streamEntry) return `Open stream entry #${item.streamEntry.id}`;
  if (item.taskId != null) return `Open task #${item.taskId}`;
  return 'Thought activity';
}

function openItem(
  item: ThoughtFeedItem,
  onOpenRun: (run: SubagentRunSummary) => void,
  onOpenStream: (entry: AgentStreamEntry) => void,
  onOpenTask: (taskId: number, projectId?: string | null) => void,
) {
  if (item.run) {
    onOpenRun(item.run);
    return;
  }
  if (item.streamEntry) {
    onOpenStream(item.streamEntry);
    return;
  }
  if (item.taskId != null) {
    onOpenTask(item.taskId, item.projectId);
  }
}

export function ThoughtFeed({
  items,
  isGlobal,
  loading,
  error,
  showRawReasoning,
  rawReasoningAvailable,
  onOpenTask,
  onOpenRun,
  onOpenStream,
}: Props) {
  if (error) {
    return <div className="empty">Thoughts failed to load: {error.message}</div>;
  }

  if (items.length === 0) {
    return <div className="empty">{loading ? 'Loading thoughts...' : 'No reasoning or assistant-message activity.'}</div>;
  }

  return (
    <div className="stream-list thought-list">
      {!rawReasoningAvailable && showRawReasoning && (
        <div className="thought-mode-note">No local raw reasoning previews are present in this feed.</div>
      )}
      {items.map(item => {
        const summary = summarizeThoughtItem(item, showRawReasoning);
        return (
          <div
            key={item.id}
            className={`stream-item thought-item thought-item-${item.kind}`}
            onClick={() => openItem(item, onOpenRun, onOpenStream, onOpenTask)}
            title={itemTitle(item)}
          >
            <div className="stream-topline thought-topline">
              <span className="message-time">{formatTimeAgo(item.createdAt)}</span>
              {isGlobal && item.projectId && (
                <span className="message-project-tag">[{truncate(item.projectId, 14)}]</span>
              )}
              <span className={`stream-chip thought-chip thought-chip-${item.kind}`}>{thoughtKindLabel(item.kind)}</span>
              <span className="stream-chip stream-chip-event">{thoughtSourceLabel(item.source)}</span>
              {item.agent && <span className="stream-sender">{truncate(item.agent, 18)}</span>}
              {item.role && <span className="thought-role">{truncate(item.role, 18)}</span>}
              {item.rawPreviewAvailable && <span className="stream-chip thought-chip-raw">raw local</span>}
              {item.kind === 'reasoning' && item.reasoningSummaryPreview && <span className="stream-chip thought-chip-summary">summary</span>}
              {item.kind === 'reasoning' && item.reasoningRedacted !== false && <span className="stream-chip thought-chip-redacted">redacted</span>}
            </div>

            <div className="stream-summary thought-summary">
              {truncate(summary.replace(/\s+/g, ' ').trim(), isGlobal ? 150 : 170)}
            </div>

            <div className="stream-links thought-links">
              {item.taskId != null && (
                <button
                  type="button"
                  className="stream-link"
                  onClick={event => {
                    event.stopPropagation();
                    onOpenTask(item.taskId!, item.projectId);
                  }}
                >
                  Task #{item.taskId}
                </button>
              )}
              {item.run && (
                <button
                  type="button"
                  className="stream-link"
                  onClick={event => {
                    event.stopPropagation();
                    onOpenRun(item.run!);
                  }}
                >
                  Run {item.run.run_id.slice(0, 8)}
                </button>
              )}
              {item.streamEntry && (
                <button
                  type="button"
                  className="stream-link"
                  onClick={event => {
                    event.stopPropagation();
                    onOpenStream(item.streamEntry!);
                  }}
                >
                  Stream #{item.streamEntry.id}
                </button>
              )}
              {item.model && <span className="thought-model">{truncate(item.model, 24)}</span>}
            </div>
          </div>
        );
      })}
    </div>
  );
}
