import type { SubagentRunSummary } from '../api/types';
import {
  formatInfrastructureFailureReason,
  formatSubagentDuration,
  formatSubagentOperatorEventName,
  formatSubagentUsageSummary,
  summarizeSubagentRunEntry,
  type SubagentRunFilter,
} from '../subagentRuns';
import { formatTimeAgo, truncate } from '../utils';

interface Props {
  runs: SubagentRunSummary[];
  totalCount: number;
  isGlobal: boolean;
  filter: SubagentRunFilter;
  limit: number;
  loading: boolean;
  error: Error | null;
  onFilterChange: (filter: SubagentRunFilter) => void;
  onLimitChange: (limit: number) => void;
  onRefresh: () => void;
  onSelectRun: (run: SubagentRunSummary) => void;
  onOpenTask: (taskId: number, projectId?: string | null) => void;
}

const SUBAGENT_LIMITS = [8, 20, 50];

export function SubagentRunPanel({
  runs,
  totalCount,
  isGlobal,
  filter,
  limit,
  loading,
  error,
  onFilterChange,
  onLimitChange,
  onRefresh,
  onSelectRun,
  onOpenTask,
}: Props) {
  return (
    <div className="subagent-run-panel">
      <div className="subagent-run-controls">
        <label className="panel-filter-label" htmlFor="subagent-run-filter">Show</label>
        <select
          id="subagent-run-filter"
          className="panel-filter-select"
          value={filter}
          onChange={event => onFilterChange(event.target.value as SubagentRunFilter)}
        >
          <option value="all">All</option>
          <option value="active">Active</option>
          <option value="problem">Problems</option>
          <option value="complete">Complete</option>
        </select>
        <label className="panel-filter-label" htmlFor="subagent-run-limit">Limit</label>
        <select
          id="subagent-run-limit"
          className="panel-filter-select"
          value={limit}
          onChange={event => onLimitChange(Number(event.target.value))}
        >
          {SUBAGENT_LIMITS.map(value => <option key={value} value={value}>{value}</option>)}
        </select>
        <button type="button" className="subagent-control-button" onClick={onRefresh}>
          Refresh
        </button>
        <span className="subagent-control-count">{runs.length}/{totalCount}</span>
        {loading && <span className="subagent-control-count">refreshing</span>}
      </div>

      {error && <div className="subagent-error-banner">Run refresh failed: {error.message}</div>}

      {runs.length === 0 ? (
        <div className="empty">No matching sub-agent runs.</div>
      ) : (
        <div className="subagent-run-list">
          {runs.map(run => (
            <div
              key={run.run_id}
              className="subagent-run-item"
              onClick={() => onSelectRun(run)}
            >
              <div className="subagent-run-topline">
                <span className={`subagent-state subagent-state-${run.state}`}>{run.state}</span>
                <span className="subagent-role">{run.role ?? 'subagent'}</span>
                <span className="subagent-run-id">{run.run_id.slice(0, 12)}</span>
                <span className="subagent-run-time">{formatTimeAgo(run.latest.created_at)}</span>
              </div>

              <div className="subagent-run-summary">
                {truncate(summarizeSubagentRunEntry(run.latest), isGlobal ? 118 : 132)}
              </div>

              <div className="subagent-run-meta">
                {isGlobal && run.project_id && <span>{truncate(run.project_id, 16)}</span>}
                {run.backend && <span>{run.backend}</span>}
                {run.model && <span>{truncate(run.model, 24)}</span>}
                {run.purpose && <span>{truncate(run.purpose, 18)}</span>}
                {run.branch && <span>{truncate(run.branch, 28)}</span>}
                {run.output_status && <span>{run.output_status}</span>}
                {run.timeout_kind && <span>{run.timeout_kind}</span>}
                {run.infrastructure_failure_reason && <span>{formatInfrastructureFailureReason(run.infrastructure_failure_reason)}</span>}
                {run.infrastructure_warning_reason && <span>warn: {formatInfrastructureFailureReason(run.infrastructure_warning_reason)}</span>}
                {run.exit_code != null && <span>exit {run.exit_code}</span>}
                {run.signal && <span>{run.signal}</span>}
                {run.heartbeat_count > 0 && <span>{run.heartbeat_count} beats</span>}
                {run.assistant_output_count > 0 && <span>{run.assistant_output_count} outputs</span>}
                {run.duration_ms != null && <span>{formatSubagentDuration(run.duration_ms)}</span>}
                {formatSubagentUsageSummary(run) && <span>{formatSubagentUsageSummary(run)}</span>}
                {run.operator_events.length > 0 && <span>{formatSubagentOperatorEventName(run.operator_events.at(-1)!.event_name)}</span>}
                {run.event_counts.raw_work > 0 && <span>{run.event_counts.raw_work} raw work hidden</span>}
                <span>{run.event_count} events</span>
              </div>

              {(run.task_id != null || run.artifact_dir) && (
                <div className="stream-links">
                  {run.task_id != null && (
                    <button
                      type="button"
                      className="stream-link"
                      onClick={event => {
                        event.stopPropagation();
                        onOpenTask(run.task_id!, run.project_id);
                      }}
                    >
                      Task #{run.task_id}
                    </button>
                  )}
                  {run.final_head_commit && <span className="subagent-artifact-path">head {run.final_head_commit.slice(0, 12)}{run.final_head_status ? ` (${run.final_head_status})` : ''}</span>}
                  {run.artifact_dir && <span className="subagent-artifact-path">{truncate(run.artifact_dir, 68)}</span>}
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
