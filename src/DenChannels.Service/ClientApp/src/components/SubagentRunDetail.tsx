import { useCallback, useMemo, useState } from 'react';
import { controlSubagentRun, getSubagentRun, type SubagentRunControlAction } from '../api/client';
import type { AgentStreamEntry, SubagentRunSummary } from '../api/types';
import { usePolling } from '../hooks/usePolling';
import {
  formatInfrastructureFailureReason,
  formatSubagentDuration,
  formatSubagentOperatorEventName,
  formatSubagentUsageSummary,
  formatSubagentWorkTimestamp,
  groupSubagentWorkEvents,
  summarizeSubagentRunEntry,
  summarizeSubagentWorkActivity,
  summarizeSubagentWorkCard,
} from '../subagentRuns';
import { truncate } from '../utils';

interface Props {
  run: SubagentRunSummary;
  onClose: () => void;
  onOpenTask: (taskId: number, projectId?: string | null) => void;
  onOpenEntry: (entry: AgentStreamEntry) => void;
}

function formatLabel(value: string): string {
  return value.replace(/_/g, ' ');
}

function formatDate(iso: string): string {
  return new Date(`${iso}Z`).toLocaleString();
}

function formatCardStatus(status: string): string {
  return status.replace(/_/g, ' ');
}

function cardTimestamp(card: { endedAt: number | null; timestamp: number | null; startedAt: number | null }): number | null {
  return card.endedAt ?? card.timestamp ?? card.startedAt;
}

export function SubagentRunDetail({ run, onClose, onOpenTask, onOpenEntry }: Props) {
  const [copiedLabel, setCopiedLabel] = useState<string | null>(null);
  const [controlPending, setControlPending] = useState<SubagentRunControlAction | null>(null);
  const [controlError, setControlError] = useState<string | null>(null);
  const fetchRun = useCallback(
    () => getSubagentRun(run.run_id, {
      projectId: run.project_id ?? undefined,
      taskId: run.task_id ?? undefined,
    }),
    [run.project_id, run.run_id, run.task_id],
  );
  const { data: detail, error, refresh } = usePolling(fetchRun, run.state === 'running' || run.state === 'retrying' || run.state === 'aborting' ? 2000 : 10_000);
  const summary = detail?.summary ?? run;
  const events = detail?.events ?? [run.latest];
  const workEvents = detail?.work_events;
  const workCards = useMemo(() => groupSubagentWorkEvents(workEvents ?? []), [workEvents]);
  const workActivity = useMemo(() => summarizeSubagentWorkActivity(workEvents ?? []), [workEvents]);
  const artifacts = detail?.artifacts ?? null;
  const canAbort = summary.state === 'running' || summary.state === 'retrying' || summary.state === 'aborting';
  const canRequestRerun = !canAbort && summary.state !== 'rerun_requested';
  const handleCopy = useCallback(async (label: string, value: string) => {
    await navigator.clipboard.writeText(value);
    setCopiedLabel(label);
    window.setTimeout(() => setCopiedLabel(current => current === label ? null : current), 1400);
  }, []);
  const handleControl = useCallback(async (action: SubagentRunControlAction) => {
    const label = action === 'abort' ? 'Abort' : 'Request rerun for';
    if (!window.confirm(`${label} sub-agent run ${summary.run_id}?`)) return;

    setControlPending(action);
    setControlError(null);
    try {
      await controlSubagentRun(summary.run_id, {
        action,
        projectId: summary.project_id ?? undefined,
        taskId: summary.task_id ?? undefined,
        requestedBy: 'web-ui',
      });
      refresh();
    } catch (e) {
      setControlError(e instanceof Error ? e.message : String(e));
    } finally {
      setControlPending(null);
    }
  }, [refresh, summary.project_id, summary.run_id, summary.task_id]);

  return (
    <div className="detail-overlay detail-overlay-wide">
      <div className="detail-header">
        <h2>Sub-agent run</h2>
        <button className="detail-close" onClick={onClose}>✕</button>
      </div>
      <div className="detail-body">
        <div className="detail-section">
          <dl className="detail-meta">
            <dt>Run</dt>
            <dd className="mono-value">{summary.run_id}</dd>
            <dt>State</dt>
            <dd><span className={`subagent-state subagent-state-${summary.state}`}>{summary.state}</span></dd>
            {summary.role && (
              <>
                <dt>Role</dt>
                <dd>{summary.role}</dd>
              </>
            )}
            {summary.backend && (
              <>
                <dt>Backend</dt>
                <dd>{summary.backend}</dd>
              </>
            )}
            {summary.schema && (
              <>
                <dt>Schema</dt>
                <dd>{summary.schema}{summary.schema_version != null ? ` v${summary.schema_version}` : ''}</dd>
              </>
            )}
            {summary.model && (
              <>
                <dt>Model</dt>
                <dd>{summary.model}</dd>
              </>
            )}
            {summary.purpose && (
              <>
                <dt>Purpose</dt>
                <dd>{summary.purpose}</dd>
              </>
            )}
            {summary.review_round_id != null && (
              <>
                <dt>Review round</dt>
                <dd>{summary.review_round_id}</dd>
              </>
            )}
            {summary.workspace_id && (
              <>
                <dt>Workspace</dt>
                <dd>{summary.workspace_id}</dd>
              </>
            )}
            {summary.branch && (
              <>
                <dt>Branch</dt>
                <dd>{summary.branch}</dd>
              </>
            )}
            {summary.final_head_commit && (
              <>
                <dt>Final head</dt>
                <dd className="mono-value">{summary.final_head_commit}{summary.final_head_status ? ` (${summary.final_head_status})` : ''}</dd>
              </>
            )}
            {summary.pid != null && (
              <>
                <dt>PID</dt>
                <dd>{summary.pid}</dd>
              </>
            )}
            {summary.exit_code != null && (
              <>
                <dt>Exit</dt>
                <dd>{summary.exit_code}{summary.signal ? ` (${summary.signal})` : ''}</dd>
              </>
            )}
            {summary.output_status && (
              <>
                <dt>Output</dt>
                <dd>{summary.output_status}</dd>
              </>
            )}
            {summary.timeout_kind && (
              <>
                <dt>Timeout</dt>
                <dd>{summary.timeout_kind}</dd>
              </>
            )}
            {summary.infrastructure_failure_reason && (
              <>
                <dt>Infrastructure</dt>
                <dd>{formatInfrastructureFailureReason(summary.infrastructure_failure_reason)}</dd>
              </>
            )}
            {summary.infrastructure_warning_reason && (
              <>
                <dt>Warning</dt>
                <dd>{formatInfrastructureFailureReason(summary.infrastructure_warning_reason)}</dd>
              </>
            )}
            {summary.fallback_from_model && (
              <>
                <dt>Fallback</dt>
                <dd>{summary.fallback_from_model} → {summary.fallback_model ?? 'configured fallback'}{summary.fallback_from_exit_code != null ? ` (exit ${summary.fallback_from_exit_code})` : ''}</dd>
              </>
            )}
            {(summary.heartbeat_count > 0 || summary.assistant_output_count > 0) && (
              <>
                <dt>Progress</dt>
                <dd>{summary.heartbeat_count} heartbeats, {summary.assistant_output_count} outputs</dd>
              </>
            )}
            {summary.duration_ms != null && (
              <>
                <dt>Duration</dt>
                <dd>{formatSubagentDuration(summary.duration_ms)}</dd>
              </>
            )}
            {formatSubagentUsageSummary(summary) && (
              <>
                <dt>Usage</dt>
                <dd>{formatSubagentUsageSummary(summary)}</dd>
              </>
            )}
            {summary.project_id && (
              <>
                <dt>Project</dt>
                <dd>{summary.project_id}</dd>
              </>
            )}
            <dt>Events</dt>
            <dd>{summary.event_count} total, {summary.event_counts.raw_work} raw work/debug</dd>
          </dl>
        </div>

        <div className="detail-section">
          <h3>Links</h3>
          <div className="detail-action-row">
            {canAbort && (
              <button
                type="button"
                className="dispatch-action dispatch-action-reject"
                disabled={controlPending !== null}
                onClick={() => void handleControl('abort')}
              >
                {controlPending === 'abort' ? 'Aborting...' : 'Abort'}
              </button>
            )}
            {canRequestRerun && (
              <button
                type="button"
                className="dispatch-action dispatch-action-approve"
                disabled={controlPending !== null}
                onClick={() => void handleControl('rerun')}
              >
                {controlPending === 'rerun' ? 'Requesting...' : 'Request rerun'}
              </button>
            )}
            <button type="button" className="dispatch-action" onClick={() => void handleCopy('run', summary.run_id)}>
              {copiedLabel === 'run' ? 'Copied run' : 'Copy run'}
            </button>
            <button type="button" className="dispatch-action" onClick={() => onOpenEntry(summary.latest)}>
              Open latest op
            </button>
            {summary.started && summary.started.id !== summary.latest.id && (
              <button type="button" className="dispatch-action" onClick={() => onOpenEntry(summary.started!)}>
                Open start op
              </button>
            )}
            {summary.task_id != null && (
              <button type="button" className="dispatch-action" onClick={() => onOpenTask(summary.task_id!, summary.project_id)}>
                Open task #{summary.task_id}
              </button>
            )}
            {summary.artifact_dir && (
              <button type="button" className="dispatch-action" onClick={() => void handleCopy('artifacts', summary.artifact_dir!)}>
                {copiedLabel === 'artifacts' ? 'Copied artifacts' : 'Copy artifacts'}
              </button>
            )}
            {summary.artifact_dir && <span className="subagent-detail-artifact">{summary.artifact_dir}</span>}
          </div>
        </div>

        {controlError && (
          <div className="detail-section">
            <h3>Control</h3>
            <div className="detail-error">Control request failed: {controlError}</div>
          </div>
        )}

        {error && (
          <div className="detail-section">
            <h3>Run Detail</h3>
            <div className="detail-description">Could not refresh run detail: {error.message}</div>
          </div>
        )}

        {summary.operator_events.length > 0 && (
          <div className="detail-section">
            <h3>Operator lifecycle</h3>
            <div className="subagent-work-card-list">
              {summary.operator_events.map((event, index) => (
                <div key={`${event.event_name}:${event.stream_entry_id ?? index}`} className="subagent-work-card subagent-work-card-lifecycle">
                  <div className="subagent-work-card-topline">
                    <span className="subagent-work-card-title">{formatSubagentOperatorEventName(event.event_name)}</span>
                    <span className="subagent-work-card-time">{event.occurred_at ? new Date(event.occurred_at).toLocaleString() : ''}</span>
                  </div>
                  <div className="subagent-work-card-preview">
                    {event.source}{event.source_event_type ? ` · ${event.source_event_type}` : ''}{event.visibility === 'debug' ? ' · debug' : ''}
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}

        {summary.stderr_preview && (
          <div className="detail-section">
            <h3>Stderr</h3>
            <pre className="detail-pre">{summary.stderr_preview}</pre>
          </div>
        )}

        {workEvents && workEvents.length > 0 && (
          <div className="detail-section">
            <h3>Work</h3>
            <div className="subagent-work-summary-grid">
              <div className="subagent-work-summary-card">
                <span>Activity</span>
                <strong>{workActivity.statusText}</strong>
              </div>
              <div className="subagent-work-summary-card">
                <span>Tools</span>
                <strong>{workActivity.toolCallCount}</strong>
              </div>
              <div className="subagent-work-summary-card">
                <span>Assistant</span>
                <strong>{workActivity.assistantMessageCount}</strong>
              </div>
              <div className="subagent-work-summary-card">
                <span>Reasoning</span>
                <strong>{workActivity.reasoningCount}</strong>
              </div>
              <div className={`subagent-work-summary-card ${workActivity.errorCount > 0 ? 'subagent-work-summary-card-error' : ''}`}>
                <span>Errors</span>
                <strong>{workActivity.errorCount}</strong>
              </div>
            </div>
            {workActivity.lastAssistantPreview && (
              <div className="subagent-work-last-assistant">
                <span>Last assistant</span>
                <p>{truncate(workActivity.lastAssistantPreview, 240)}</p>
              </div>
            )}
            <div className="subagent-work-card-list">
              {workCards.map(card => (
                <article key={card.id} className={`subagent-work-card subagent-work-card-${card.kind} subagent-work-card-${card.status}`}>
                  <div className="subagent-work-card-head">
                    <span className="stream-chip stream-chip-event">{card.kind}</span>
                    <strong>{card.title}</strong>
                    <span className={`subagent-work-status subagent-work-status-${card.status}`}>{formatCardStatus(card.status)}</span>
                    <span className="subagent-event-time">{formatSubagentWorkTimestamp(cardTimestamp(card))}</span>
                  </div>
                  {card.warning && <div className="subagent-work-warning">⚠ {card.warning}</div>}
                  {card.textPreview && <p className="subagent-work-text">{truncate(card.textPreview, 420)}</p>}
                  {card.argsPreview && (
                    <div className="subagent-work-preview">
                      <span>Args</span>
                      <pre>{truncate(card.argsPreview, 520)}</pre>
                    </div>
                  )}
                  {card.resultPreview && (
                    <div className="subagent-work-preview">
                      <span>{card.status === 'error' ? 'Error/result' : 'Result'}</span>
                      <pre>{truncate(card.resultPreview, 520)}</pre>
                    </div>
                  )}
                  <details className="subagent-work-raw">
                    <summary>{card.eventCount} raw event{card.eventCount === 1 ? '' : 's'} · {truncate(summarizeSubagentWorkCard(card), 96)}</summary>
                    <pre>{JSON.stringify(card.events, null, 2)}</pre>
                  </details>
                </article>
              ))}
            </div>
          </div>
        )}

        {artifacts && (
          <div className="detail-section">
            <h3>Artifacts</h3>
            {artifacts.read_error && <div className="detail-description">{artifacts.read_error}</div>}
            {artifacts.status_json != null && (
              <details className="artifact-detail" open>
                <summary>status.json</summary>
                <pre className="detail-pre">{artifacts.status_json}</pre>
              </details>
            )}
            {artifacts.events_tail != null && (
              <details className="artifact-detail">
                <summary>events.jsonl</summary>
                <pre className="detail-pre">{artifacts.events_tail}</pre>
              </details>
            )}
            {artifacts.stdout_tail != null && (
              <details className="artifact-detail">
                <summary>stdout.jsonl</summary>
                <pre className="detail-pre">{artifacts.stdout_tail}</pre>
              </details>
            )}
            {artifacts.stderr_tail != null && (
              <details className="artifact-detail">
                <summary>stderr.log</summary>
                <pre className="detail-pre">{artifacts.stderr_tail}</pre>
              </details>
            )}
            {artifacts.session_tail != null && (
              <details className="artifact-detail">
                <summary>{artifacts.session_file_path ? `session ${artifacts.session_file_path}` : 'session.jsonl'}</summary>
                <pre className="detail-pre">{artifacts.session_tail}</pre>
              </details>
            )}
          </div>
        )}

        <div className="detail-section">
          <h3>Lifecycle</h3>
          <div className="subagent-event-list">
            {events.map(event => (
              <button
                key={event.id}
                type="button"
                className="subagent-event-item"
                onClick={() => onOpenEntry(event)}
              >
                <span className="subagent-event-time">{formatDate(event.created_at)}</span>
                <span className="stream-chip stream-chip-event">{formatLabel(event.event_type)}</span>
                <span className="subagent-event-body">{truncate(summarizeSubagentRunEntry(event), 140)}</span>
              </button>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
