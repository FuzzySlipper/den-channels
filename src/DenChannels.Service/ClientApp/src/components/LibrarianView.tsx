import { useMemo, useState, type FormEvent } from 'react';
import type { DocumentSummary, LibrarianResponse, RelevantItem, Space } from '../api/types';
import { queryLibrarian } from '../api/client';
import {
  documentRefFromLibrarianItem,
  groupLibrarianItems,
  librarianHasResults,
  messageRefFromLibrarianItem,
  stableLibrarianRef,
  taskRefFromLibrarianItem,
} from '../librarian';

interface Props {
  projects: Space[];
  currentProjectId: string | null;
  onOpenTask: (taskId: number, projectId?: string | null) => void;
  onOpenDocument: (doc: DocumentSummary) => void;
  onOpenMessage?: (projectId: string, messageId: number) => void;
  onOpenThread?: (projectId: string, threadId: number) => void;
}

export function LibrarianView({ projects, currentProjectId, onOpenTask, onOpenDocument, onOpenMessage, onOpenThread }: Props) {
  const [query, setQuery] = useState('');
  const [scope, setScope] = useState<'current' | '_global'>('current');
  const [taskIdText, setTaskIdText] = useState('');
  const [includeGlobal, setIncludeGlobal] = useState(true);
  const [response, setResponse] = useState<LibrarianResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const targetProjectId = scope === '_global' ? '_global' : currentProjectId;
  const effectiveIncludeGlobal = targetProjectId === '_global' ? false : includeGlobal;
  const taskId = useMemo(() => {
    const trimmed = taskIdText.trim();
    if (!trimmed) return undefined;
    return /^\d+$/.test(trimmed) ? Number(trimmed) : Number.NaN;
  }, [taskIdText]);
  const canSubmit = Boolean(targetProjectId && query.trim() && !loading && !Number.isNaN(taskId));

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!targetProjectId || !canSubmit) return;

    setLoading(true);
    setError(null);
    try {
      const result = await queryLibrarian(targetProjectId, {
        query: query.trim(),
        taskId,
        includeGlobal: effectiveIncludeGlobal,
      });
      setResponse(result);
    } catch (err) {
      setResponse(null);
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="librarian-view">
      <form className="librarian-query" onSubmit={event => void handleSubmit(event)}>
        <div className="librarian-query-row">
          <label htmlFor="librarian-query-input">Ask the librarian</label>
          <textarea
            id="librarian-query-input"
            value={query}
            onChange={event => setQuery(event.target.value)}
            placeholder="Find relevant docs, task context, messages, or guidance..."
            rows={3}
          />
        </div>

        <div className="librarian-query-controls">
          <label>
            Scope
            <select value={scope} onChange={event => setScope(event.target.value as 'current' | '_global')}>
              <option value="current">Current space{currentProjectId ? ` (${currentProjectId})` : ''}</option>
              <option value="_global">_global</option>
            </select>
          </label>

          <label>
            Task #
            <input
              value={taskIdText}
              onChange={event => setTaskIdText(event.target.value)}
              placeholder="optional"
              inputMode="numeric"
            />
          </label>

          <label className="librarian-checkbox">
            <input
              type="checkbox"
              checked={effectiveIncludeGlobal}
              onChange={event => setIncludeGlobal(event.target.checked)}
              disabled={targetProjectId === '_global'}
            />
            Include _global docs
          </label>

          <button type="submit" className="dispatch-action dispatch-action-approve" disabled={!canSubmit}>
            {loading ? 'Querying...' : 'Query'}
          </button>
        </div>

        {Number.isNaN(taskId) && (
          <div className="detail-error" role="alert">Task id must be a number.</div>
        )}
        {!targetProjectId && <div className="empty">Select a space before querying the librarian.</div>}
      </form>

      {error && <div className="detail-error" role="alert">Librarian query failed: {error}</div>}
      {loading && <div className="loading">Asking librarian...</div>}
      {!loading && response && !librarianHasResults(response) && (
        <div className="empty">No librarian results.</div>
      )}
      {!loading && response && librarianHasResults(response) && (
        <LibrarianResults
          response={response}
          projects={projects}
          fallbackProjectId={targetProjectId}
          onOpenTask={onOpenTask}
          onOpenDocument={onOpenDocument}
          onOpenMessage={onOpenMessage}
          onOpenThread={onOpenThread}
        />
      )}
    </div>
  );
}

interface ResultsProps {
  response: LibrarianResponse;
  projects: Space[];
  fallbackProjectId: string | null;
  onOpenTask: (taskId: number, projectId?: string | null) => void;
  onOpenDocument: (doc: DocumentSummary) => void;
  onOpenMessage?: (projectId: string, messageId: number) => void;
  onOpenThread?: (projectId: string, threadId: number) => void;
}

function LibrarianResults({ response, projects, fallbackProjectId, onOpenTask, onOpenDocument, onOpenMessage, onOpenThread }: ResultsProps) {
  const groups = groupLibrarianItems(response.relevant_items);
  return (
    <div className="librarian-results">
      <div className="librarian-result-summary">
        <span className={`stream-chip librarian-confidence librarian-confidence-${response.confidence}`}>
          {response.confidence} confidence
        </span>
        <span>{response.relevant_items.length} source{response.relevant_items.length === 1 ? '' : 's'}</span>
      </div>

      {groups.map(group => (
        <section className="librarian-group" key={group.key}>
          <h3>{group.label}</h3>
          <div className="librarian-item-list">
            {group.items.map((item, index) => (
              <LibrarianResultItem
                key={`${group.key}:${item.source_id}:${index}`}
                item={item}
                projects={projects}
                fallbackProjectId={fallbackProjectId}
                onOpenTask={onOpenTask}
                onOpenDocument={onOpenDocument}
                onOpenMessage={onOpenMessage}
                onOpenThread={onOpenThread}
              />
            ))}
          </div>
        </section>
      ))}

      {response.recommendations.length > 0 && (
        <section className="librarian-group">
          <h3>Recommendations</h3>
          <ul className="librarian-recommendations">
            {response.recommendations.map((recommendation, index) => (
              <li key={`${index}:${recommendation}`}>{recommendation}</li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

interface ItemProps {
  item: RelevantItem;
  projects: Space[];
  fallbackProjectId: string | null;
  onOpenTask: (taskId: number, projectId?: string | null) => void;
  onOpenDocument: (doc: DocumentSummary) => void;
  onOpenMessage?: (projectId: string, messageId: number) => void;
  onOpenThread?: (projectId: string, threadId: number) => void;
}

function LibrarianResultItem({ item, projects, fallbackProjectId, onOpenTask, onOpenDocument, onOpenMessage, onOpenThread }: ItemProps) {
  const docRef = documentRefFromLibrarianItem(item);
  const taskRef = taskRefFromLibrarianItem(item);
  const messageRef = messageRefFromLibrarianItem(item);
  const projectId = item.project_id ?? docRef?.projectId ?? taskRef?.projectId ?? messageRef?.projectId ?? fallbackProjectId;
  const project = projects.find(candidate => candidate.id === projectId);

  return (
    <article className="librarian-item">
      <div className="librarian-item-head">
        <span className="stream-chip stream-chip-event">{item.type}</span>
        <span className="mono-value">{stableLibrarianRef(item)}</span>
        {projectId && <span className="message-project-tag">[{projectId}]</span>}
      </div>
      <p className="librarian-item-summary">{item.summary}</p>
      <p className="librarian-item-why">{item.why_relevant}</p>
      {item.snippet && <blockquote>{item.snippet}</blockquote>}
      <div className="stream-links librarian-links">
        {docRef && (
          <button
            type="button"
            className="stream-link"
            onClick={() => onOpenDocument({
              id: 0,
              project_id: docRef.projectId,
              slug: docRef.slug,
              title: item.summary || docRef.slug,
              doc_type: 'note',
              tags: null,
              updated_at: '',
            })}
          >
            Open doc
          </button>
        )}
        {taskRef && (
          <button
            type="button"
            className="stream-link"
            onClick={() => onOpenTask(taskRef.taskId, taskRef.projectId ?? projectId)}
          >
            Open task #{taskRef.taskId}
          </button>
        )}
        {messageRef && projectId && onOpenMessage && (
          <button
            type="button"
            className="stream-link"
            onClick={() => onOpenMessage(projectId, messageRef.messageId)}
          >
            Open message #{messageRef.messageId}
          </button>
        )}
        {messageRef?.threadId && projectId && onOpenThread && (
          <button
            type="button"
            className="stream-link"
            onClick={() => onOpenThread(projectId, messageRef.threadId!)}
          >
            Open thread #{messageRef.threadId}
          </button>
        )}
        {project && <span className="thought-model">{project.name}</span>}
      </div>
    </article>
  );
}
