import { useEffect, useState } from 'react';
import type {
  AgentWorkspace,
  GitStatusResponse,
  Message,
  ReviewTimelineEntry,
  ReviewVerdict,
  SubagentRunState,
  SubagentRunSummary,
  TaskDetail as TaskDetailType,
  TaskStatus,
} from '../api/types';
import { getProjectGitStatus, getTask, getWorkspaceGitStatus, listProjectAgentWorkspaces, listSubagentRuns, updateTask } from '../api/client';
import { formatSubagentDuration } from '../subagentRuns';
import { formatTimeAgo, truncate } from '../utils';
import { messageIntentLabel } from '../messageIntents';
import { buildTaskGitFocus, dirtyCount, reviewGitAlignmentWarnings, summarizeGitStatus, type GitFocus } from '../git';
import { renderFindingMeta } from '../reviewFindings';

interface Props {
  projectId: string;
  taskId: number;
  onSelectTask: (taskId: number) => void;
  onSelectMessage: (message: Message) => void;
  onSelectRun: (run: SubagentRunSummary) => void;
  onOpenGit: (focus: GitFocus) => void;
  onClose: () => void;
}

const STATUSES: TaskStatus[] = ['planned', 'in_progress', 'review', 'blocked', 'done', 'cancelled'];

function formatLabel(value: string): string {
  return value.replace(/_/g, ' ');
}

function formatVerdict(verdict: ReviewVerdict | null): string {
  return verdict ? formatLabel(verdict) : 'pending';
}

function formatDelta(detail: TaskDetailType): string {
  const delta = detail.review_workflow.current_round?.delta_diff;
  return delta?.base_commit ? `${delta.base_commit}..${delta.head_commit}` : '(initial review)';
}

function formatTimeline(entry: ReviewTimelineEntry): string {
  const parts = [`${entry.total_finding_count} findings`];
  if (entry.open_finding_count > 0) parts.push(`${entry.open_finding_count} open`);
  if (entry.addressed_finding_count > 0) parts.push(`${entry.addressed_finding_count} addressed`);
  if (entry.resolved_finding_count > 0) parts.push(`${entry.resolved_finding_count} resolved`);
  return parts.join(' · ');
}

function isActiveRunState(state: SubagentRunState): boolean {
  return state === 'running' || state === 'retrying' || state === 'aborting' || state === 'rerun_requested';
}

function isProblemRunState(state: SubagentRunState): boolean {
  return state === 'failed' || state === 'timeout' || state === 'aborted' || state === 'unknown';
}

function shortSha(value: string | null | undefined): string {
  return value ? value.slice(0, 8) : 'unknown';
}

function summarizeRun(run: SubagentRunSummary): string {
  const bits = [run.role, run.model, run.duration_ms != null ? formatSubagentDuration(run.duration_ms) : null]
    .filter(Boolean);
  return bits.length > 0 ? bits.join(' · ') : run.run_id;
}

function selectRelevantWorkspace(workspaces: AgentWorkspace[], branch?: string | null): AgentWorkspace | null {
  if (workspaces.length === 0) return null;
  if (branch) {
    const byBranch = workspaces.find(workspace => workspace.branch === branch);
    if (byBranch) return byBranch;
  }
  return workspaces[0];
}

function nextAction(detail: TaskDetailType, runs: SubagentRunSummary[]): string {
  const activeRun = runs.find(run => isActiveRunState(run.state));
  const latestRun = runs[0] ?? null;
  const currentProblemRun = latestRun && isProblemRunState(latestRun.state) ? latestRun : null;
  const currentRound = detail.review_workflow.current_round;
  if (activeRun) return `Monitor ${activeRun.role ?? 'agent'} run ${activeRun.run_id.slice(0, 8)} or abort if it drifts.`;
  if (currentProblemRun) return `Inspect latest ${currentProblemRun.state} run ${currentProblemRun.run_id.slice(0, 8)} and rerun or fix the task.`;
  if (detail.task.status === 'blocked') return 'Resolve dependencies or unblock the task before assigning more work.';
  if (detail.open_review_findings.length > 0) return 'Address open review findings, then request rereview.';
  if (detail.task.status === 'review' && currentRound?.verdict === 'looks_good') return 'Confirm the reviewed head still matches, then merge.';
  if (detail.task.status === 'review') return 'Wait for review or launch a reviewer run if none is active.';
  if (detail.task.status === 'planned') return 'Claim/start implementation on a task branch.';
  if (detail.task.status === 'in_progress') return 'Continue implementation, then request review with a stable diff.';
  if (detail.task.status === 'done') return 'No action needed; task is complete.';
  return 'Triage task state and decide the next operator action.';
}

export function TaskDetail({ projectId, taskId, onSelectTask, onSelectMessage, onSelectRun, onOpenGit, onClose }: Props) {
  const [detail, setDetail] = useState<TaskDetailType | null>(null);
  const [runs, setRuns] = useState<SubagentRunSummary[]>([]);
  const [gitStatus, setGitStatus] = useState<GitStatusResponse | null>(null);
  const [gitWorkspace, setGitWorkspace] = useState<AgentWorkspace | null>(null);
  const [gitError, setGitError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [runsError, setRunsError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    getTask(projectId, taskId)
      .then(d => { if (!cancelled) setDetail(d); })
      .catch(e => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
  }, [projectId, taskId]);

  useEffect(() => {
    let cancelled = false;
    let timer: number | null = null;
    const loadRuns = () => {
      listSubagentRuns({ projectId, taskId, limit: 8 })
        .then(result => {
          if (!cancelled) {
            setRuns(result);
            setRunsError(null);
          }
        })
        .catch(e => {
          if (!cancelled) setRunsError(e instanceof Error ? e.message : String(e));
        });
    };
    loadRuns();
    timer = window.setInterval(loadRuns, 2000);
    return () => {
      cancelled = true;
      if (timer != null) window.clearInterval(timer);
    };
  }, [projectId, taskId]);

  useEffect(() => {
    let cancelled = false;
    async function loadGitStatus() {
      if (!detail) return;
      try {
        const workspaces = await listProjectAgentWorkspaces(projectId, { taskId, limit: 20 });
        const workspace = selectRelevantWorkspace(workspaces, detail.review_workflow.current_round?.branch);
        const status = workspace
          ? await getWorkspaceGitStatus(projectId, workspace.id)
          : await getProjectGitStatus(projectId);
        if (!cancelled) {
          setGitWorkspace(workspace);
          setGitStatus(status);
          setGitError(null);
        }
      } catch (e) {
        if (!cancelled) {
          setGitWorkspace(null);
          setGitStatus(null);
          setGitError(e instanceof Error ? e.message : String(e));
        }
      }
    }
    void loadGitStatus();
    return () => { cancelled = true; };
  }, [detail, projectId, taskId]);

  const handleStatusChange = async (newStatus: string) => {
    try {
      await updateTask(projectId, taskId, 'web-ui', { status: newStatus });
      const d = await getTask(projectId, taskId);
      setDetail(d);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  if (error) {
    return (
      <div className="detail-overlay">
        <div className="detail-header">
          <h2>Error</h2>
          <button className="detail-close" onClick={onClose}>✕</button>
        </div>
        <div className="detail-body"><div className="empty">{error}</div></div>
      </div>
    );
  }

  if (!detail) {
    return (
      <div className="detail-overlay">
        <div className="detail-header">
          <h2>Loading...</h2>
          <button className="detail-close" onClick={onClose}>✕</button>
        </div>
      </div>
    );
  }

  const { task } = detail;
  const currentRound = detail.review_workflow.current_round;
  const activeRuns = runs.filter(run => isActiveRunState(run.state));
  const latestRun = runs[0] ?? null;
  const latestProblemRun = latestRun && isProblemRunState(latestRun.state) ? latestRun : null;
  const operatorNextAction = nextAction(detail, runs);
  const gitWarnings = reviewGitAlignmentWarnings(currentRound, gitStatus);
  const gitFocus = buildTaskGitFocus(projectId, task.id, gitWorkspace, currentRound?.branch);

  const handleTaskNavigation = (nextTaskId: number) => {
    if (nextTaskId !== task.id) {
      onSelectTask(nextTaskId);
    }
  };

  return (
    <div className="detail-overlay detail-overlay-wide">
      <div className="detail-header">
        <h2>#{task.id} {task.title}</h2>
        <button className="detail-close" onClick={onClose}>✕</button>
      </div>
      <div className="detail-body">
        <div className="detail-section">
          <dl className="detail-meta">
            <dt>Status</dt>
            <dd>
              <select
                className="status-select"
                value={task.status}
                onChange={e => handleStatusChange(e.target.value)}
              >
                {STATUSES.map(s => <option key={s} value={s}>{s}</option>)}
              </select>
            </dd>
            <dt>Priority</dt>
            <dd className={`priority-${task.priority}`}>P{task.priority}</dd>
            <dt>Assigned</dt>
            <dd>{task.assigned_to ?? '(none)'}</dd>
            {task.tags && task.tags.length > 0 && (
              <>
                <dt>Tags</dt>
                <dd>{task.tags.join(', ')}</dd>
              </>
            )}
          </dl>
        </div>

        <div className="detail-section workspace-section">
          <h3>Workspace</h3>
          <div className="workspace-grid">
            <div className={`workspace-card ${activeRuns.length > 0 ? 'workspace-card-active' : latestProblemRun ? 'workspace-card-problem' : ''}`}>
              <span>Running</span>
              <strong>{activeRuns.length > 0 ? `${activeRuns.length} active run${activeRuns.length === 1 ? '' : 's'}` : latestRun ? `last ${latestRun.state}` : 'no runs yet'}</strong>
              <p>{activeRuns[0] ? summarizeRun(activeRuns[0]) : latestRun ? summarizeRun(latestRun) : 'No sub-agent activity is linked to this task yet.'}</p>
            </div>
            <button
              type="button"
              className={`workspace-card workspace-card-button ${gitStatus && dirtyCount(gitStatus) > 0 ? 'workspace-card-active' : ''}`}
              onClick={() => onOpenGit(gitFocus)}
              title="Open Git view for this task/workspace"
            >
              <span>Changed</span>
              <strong>{gitStatus ? `${dirtyCount(gitStatus)} live changed file${dirtyCount(gitStatus) === 1 ? '' : 's'}` : currentRound ? `${currentRound.preferred_diff.base_ref}...${currentRound.preferred_diff.head_ref}` : 'open Git view'}</strong>
              <p>{gitStatus ? summarizeGitStatus(gitStatus) : gitError ? `Could not load live git state: ${gitError}` : currentRound ? `head ${shortSha(currentRound.head_commit)} on ${currentRound.branch}` : 'Open the read-only Git view for project/workspace changes.'}</p>
            </button>
            <div className={`workspace-card ${detail.open_review_findings.length > 0 ? 'workspace-card-problem' : detail.review_workflow.current_verdict === 'looks_good' ? 'workspace-card-ready' : ''}`}>
              <span>Review</span>
              <strong>{currentRound ? formatVerdict(detail.review_workflow.current_verdict) : 'not requested'}</strong>
              <p>{currentRound ? `${detail.open_review_findings.length} open findings · R${currentRound.round_number}` : 'No review round is linked to this task.'}</p>
            </div>
            <div className="workspace-card workspace-card-next">
              <span>Next</span>
              <strong>{task.status.replace(/_/g, ' ')}</strong>
              <p>{operatorNextAction}</p>
            </div>
          </div>
          {gitWorkspace && <div className="detail-description">Git view target: workspace <code>{gitWorkspace.id}</code> on <code>{gitWorkspace.branch}</code>.</div>}
          {gitWarnings.length > 0 && (
            <div className="workspace-warning-list">
              {gitWarnings.map(warning => <div key={warning} className="workspace-warning">{warning}</div>)}
            </div>
          )}
          {runsError && <div className="detail-description">Could not load agent runs: {runsError}</div>}
          {runs.length > 0 && (
            <div className="workspace-run-list">
              {runs.slice(0, 4).map(run => (
                <button
                  key={run.run_id}
                  type="button"
                  className={`workspace-run-card workspace-run-card-${run.state}`}
                  onClick={() => onSelectRun(run)}
                  title={`Open run ${run.run_id}`}
                >
                  <span className={`subagent-state subagent-state-${run.state}`}>{run.state}</span>
                  <span className="workspace-run-title">{run.role ?? 'agent'} · {run.run_id.slice(0, 8)}</span>
                  <span className="workspace-run-meta">{truncate(summarizeRun(run), 90)}</span>
                </button>
              ))}
            </div>
          )}
        </div>

        {detail.review_workflow.review_round_count > 0 && currentRound && (
          <div className="detail-section">
            <h3>Review Workflow</h3>
            <dl className="detail-meta">
              <dt>Current Round</dt>
              <dd>R{currentRound.round_number} on <code>{currentRound.branch}</code></dd>
              <dt>Verdict</dt>
              <dd>
                <span className={`review-pill review-pill-${detail.review_workflow.current_verdict ?? 'pending'}`}>
                  {formatVerdict(detail.review_workflow.current_verdict)}
                </span>
              </dd>
              <dt>Reviewed Diff</dt>
              <dd><code>{currentRound.preferred_diff.base_ref}...{currentRound.preferred_diff.head_ref}</code></dd>
              <dt>Delta</dt>
              <dd><code>{formatDelta(detail)}</code></dd>
              <dt>Open Findings</dt>
              <dd>{detail.review_workflow.unresolved_finding_count}</dd>
            </dl>
          </div>
        )}

        {detail.open_review_findings.length > 0 && (
          <div className="detail-section">
            <h3>Open Review Findings</h3>
            {detail.open_review_findings.map(finding => (
              <div key={finding.id} className="review-card">
                <div className="review-card-head">
                  <strong>{finding.finding_key}</strong>
                  <div className="review-pill-row">
                    <span className={`review-pill review-pill-${finding.category}`}>{formatLabel(finding.category)}</span>
                    <span className={`review-pill review-pill-${finding.status}`}>{formatLabel(finding.status)}</span>
                  </div>
                </div>
                <div className="review-summary">{finding.summary}</div>
                {renderFindingMeta(finding).map(line => (
                  <div key={line} className="review-subtle">{line}</div>
                ))}
              </div>
            ))}
          </div>
        )}

        {detail.review_workflow.timeline.length > 0 && (
          <div className="detail-section">
            <h3>Review Timeline</h3>
            {detail.review_workflow.timeline.map(entry => (
              <div key={entry.review_round_id} className="review-card">
                <div className="review-card-head">
                  <strong>R{entry.review_round_number}</strong>
                  <span className={`review-pill review-pill-${entry.verdict ?? 'pending'}`}>
                    {formatVerdict(entry.verdict)}
                  </span>
                </div>
                <div className="review-summary">
                  <code>{entry.branch}</code> · {formatTimeline(entry)}
                </div>
                <div className="review-subtle">
                  Requested by {entry.requested_by} {formatTimeAgo(entry.requested_at)}
                  {entry.verdict_at ? ` · verdict ${formatTimeAgo(entry.verdict_at)}` : ''}
                </div>
              </div>
            ))}
          </div>
        )}

        {detail.dependencies.length > 0 && (
          <div className="detail-section">
            <h3>Dependencies</h3>
            {detail.dependencies.map(dep => (
              <button
                key={dep.task_id}
                type="button"
                className="list-item detail-nav-button"
                onClick={() => handleTaskNavigation(dep.task_id)}
                title={`Open task #${dep.task_id}`}
              >
                <span className={`badge badge-${dep.status}`}>{dep.status}</span>
                {' '}#{dep.task_id} {dep.title}
              </button>
            ))}
          </div>
        )}

        {detail.subtasks.length > 0 && (
          <div className="detail-section">
            <h3>Subtasks</h3>
            {detail.subtasks.map(sub => (
              <button
                key={sub.id}
                type="button"
                className="list-item detail-nav-button"
                onClick={() => handleTaskNavigation(sub.id)}
                title={`Open task #${sub.id}`}
              >
                <span className={`badge badge-${sub.status}`}>{sub.status}</span>
                {' '}#{sub.id} {sub.title}
              </button>
            ))}
          </div>
        )}

        {task.description && (
          <div className="detail-section">
            <h3>Description</h3>
            <div className="detail-description">{task.description}</div>
          </div>
        )}

        {detail.recent_messages.length > 0 && (
          <div className="detail-section">
            <h3>Recent Messages</h3>
            {detail.recent_messages.map(msg => (
              <button
                key={msg.id}
                type="button"
                className="message-item detail-nav-button detail-message-button"
                onClick={() => onSelectMessage(msg)}
                title={msg.thread_id != null ? `Open thread #${msg.thread_id}` : `Open message #${msg.id}`}
              >
                <span className="message-time">{formatTimeAgo(msg.created_at)}</span>
                <span className={`intent-chip intent-${msg.intent}`}>{messageIntentLabel(msg.intent)}</span>
                <span className="message-sender">{msg.sender}:</span>
                <span className="message-content">{msg.content.replace(/\n/g, ' ')}</span>
              </button>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
