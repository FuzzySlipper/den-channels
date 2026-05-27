export type WorkspaceViewMode = 'tasks' | 'messages' | 'documents' | 'git' | 'librarian' | 'agent-stream' | 'sessions' | 'agents';

interface Props {
  statusFilter: string | null;
  onStatusFilterChange: (status: string | null) => void;
  sortMode: string;
  onSortChange: (sort: string) => void;
  viewMode: WorkspaceViewMode;
  onViewModeChange: (mode: WorkspaceViewMode) => void;
}

const STATUSES = ['planned', 'in_progress', 'review', 'blocked', 'done', 'cancelled'];
const SORTS = ['priority', 'id', 'status', 'title'];

export function FilterBar({
  statusFilter, onStatusFilterChange,
  sortMode, onSortChange,
  viewMode, onViewModeChange,
}: Props) {
  return (
    <div className="filter-bar">
      {viewMode === 'tasks' && (
        <>
          <label>Filter:</label>
          <select
            value={statusFilter ?? ''}
            onChange={e => onStatusFilterChange(e.target.value || null)}
          >
            <option value="">All</option>
            {STATUSES.map(s => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>

          <label>Sort:</label>
          <select value={sortMode} onChange={e => onSortChange(e.target.value)}>
            {SORTS.map(s => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>
        </>
      )}

      <div className="view-toggle">
        <button
          className={viewMode === 'tasks' ? 'active' : ''}
          onClick={() => onViewModeChange('tasks')}
        >
          Tasks
        </button>
        <button
          className={viewMode === 'messages' ? 'active' : ''}
          onClick={() => onViewModeChange('messages')}
        >
          Messages
        </button>
        <button
          className={viewMode === 'documents' ? 'active' : ''}
          onClick={() => onViewModeChange('documents')}
        >
          Docs
        </button>
        <button
          className={viewMode === 'git' ? 'active' : ''}
          onClick={() => onViewModeChange('git')}
        >
          Git
        </button>
        <button
          className={viewMode === 'librarian' ? 'active' : ''}
          onClick={() => onViewModeChange('librarian')}
        >
          Librarian
        </button>
        <button
          className={viewMode === 'agent-stream' ? 'active' : ''}
          onClick={() => onViewModeChange('agent-stream')}
        >
          Agent Stream
        </button>
        <button
          className={viewMode === 'sessions' ? 'active' : ''}
          onClick={() => onViewModeChange('sessions')}
        >
          Sessions
        </button>
        <button
          className={viewMode === 'agents' ? 'active' : ''}
          onClick={() => onViewModeChange('agents')}
        >
          Agents
        </button>
      </div>
    </div>
  );
}
