import { useEffect, useMemo, useState } from 'react';
import type { GitDiffResponse, GitFileStatus, GitStatusResponse, Project } from '../api/types';
import {
  getProjectGitDiff,
  getProjectGitStatus,
  getWorkspaceGitDiff,
  getWorkspaceGitStatus,
  listProjectAgentWorkspaces,
} from '../api/client';
import {
  dirtyCount,
  formatAheadBehind,
  formatBranchLabel,
  gitDiffBadges,
  gitFileStatusLabel,
  gitFocusKey,
  gitNoticeCounts,
  groupGitFiles,
  pickFocusedGitTargetId,
  sameGitFile,
  shortSha,
  shouldRequestStagedDiff,
  summarizeGitStatus,
  type GitFocus,
  type GitStatusTarget,
} from '../git';

interface Props {
  projectId: string | null;
  projects: Project[];
  isGlobal: boolean;
  scopeSupportsGit?: boolean;
  focus?: GitFocus | null;
  onClearFocus?: () => void;
}

export function GitView({ projectId, projects, isGlobal, scopeSupportsGit = true, focus, onClearFocus }: Props) {
  const [targets, setTargets] = useState<GitStatusTarget[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selectedTargetId, setSelectedTargetId] = useState<string | null>(null);
  const [selectedFile, setSelectedFile] = useState<GitFileStatus | null>(null);
  const [diff, setDiff] = useState<GitDiffResponse | null>(null);
  const [diffLoading, setDiffLoading] = useState(false);
  const [diffError, setDiffError] = useState<string | null>(null);
  const [appliedFocusKey, setAppliedFocusKey] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      if (!projectId || !scopeSupportsGit) {
        setTargets([]);
        return;
      }

      setLoading(true);
      setError(null);
      try {
        const projectTargets = isGlobal
          ? projects.filter(project => project.id !== '_global')
          : projects.filter(project => project.id === projectId);
        const fallbackProjects = projectTargets.length > 0 || isGlobal
          ? projectTargets
          : [{ id: projectId, name: projectId, root_path: null, description: null, created_at: '', updated_at: '' }];

        const loaded = (await Promise.all(fallbackProjects.map(project => loadProjectTargets(project, focus)))).flat();
        if (cancelled) return;
        setTargets(loaded);
        const focusKey = gitFocusKey(focus);
        if (!focusKey && appliedFocusKey) setAppliedFocusKey(null);
        const focusedTargetId = focusKey !== appliedFocusKey ? pickFocusedGitTargetId(loaded, focus) : null;
        const nextSelectedTargetId = focusedTargetId
          ?? (selectedTargetId && loaded.some(target => target.id === selectedTargetId)
            ? selectedTargetId
            : (loaded[0]?.id ?? null));
        if (focusKey !== appliedFocusKey && focusedTargetId) setAppliedFocusKey(focusKey);
        setSelectedTargetId(nextSelectedTargetId);
        setSelectedFile(current => {
          if (!current || !nextSelectedTargetId) return null;
          const target = loaded.find(item => item.id === nextSelectedTargetId);
          return target?.status.files.some(file => sameGitFile(file, current)) ? current : null;
        });
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    void load();
    return () => { cancelled = true; };
  }, [appliedFocusKey, focus, isGlobal, projectId, projects, scopeSupportsGit, selectedTargetId]);

  const selectedTarget = useMemo(
    () => targets.find(target => target.id === selectedTargetId) ?? targets[0] ?? null,
    [selectedTargetId, targets],
  );

  useEffect(() => {
    let cancelled = false;

    async function loadDiff() {
      if (!selectedTarget || !selectedFile) {
        setDiff(null);
        return;
      }

      setDiffLoading(true);
      setDiffError(null);
      try {
        const opts = { path: selectedFile.path, maxBytes: 64 * 1024, staged: shouldRequestStagedDiff(selectedFile) };
        const next = selectedTarget.kind === 'project'
          ? await getProjectGitDiff(selectedTarget.projectId, opts)
          : await getWorkspaceGitDiff(selectedTarget.projectId, selectedTarget.workspaceId!, opts);
        if (!cancelled) setDiff(next);
      } catch (err) {
        if (!cancelled) setDiffError(err instanceof Error ? err.message : String(err));
      } finally {
        if (!cancelled) setDiffLoading(false);
      }
    }

    void loadDiff();
    return () => { cancelled = true; };
  }, [selectedFile, selectedTarget]);

  const totalDirtyFiles = targets.reduce((sum, target) => sum + dirtyCount(target.status), 0);
  const dirtyTargets = targets.filter(target => dirtyCount(target.status) > 0).length;
  const selectedGroups = selectedTarget ? groupGitFiles(selectedTarget.status.files) : [];

  return (
    <div className="git-view">
      <div className="git-summary-line">
        {focus && (
          <button className="git-focus-chip" type="button" onClick={onClearFocus} title="Clear task/workspace git focus">
            Focus task {focus.taskId ? `#${focus.taskId}` : ''}{focus.workspaceId ? ` · ${focus.workspaceId}` : ''}{focus.branch ? ` · ${focus.branch}` : ''} ✕
          </button>
        )}
        <span>{dirtyTargets} dirty target{dirtyTargets === 1 ? '' : 's'}</span>
        <span>{totalDirtyFiles} changed file{totalDirtyFiles === 1 ? '' : 's'}</span>
        {loading && <span>Refreshing git state...</span>}
        {error && <span className="git-error-text">{error}</span>}
      </div>

      <div className="git-target-grid">
        {targets.map(target => (
          <button
            key={target.id}
            className={`git-target-card${selectedTarget?.id === target.id ? ' git-target-card-selected' : ''}`}
            onClick={() => {
              setSelectedTargetId(target.id);
              setSelectedFile(null);
              setDiff(null);
            }}
          >
            <span className="git-target-kind">{target.kind}</span>
            <strong>{target.title}</strong>
            <span>{summarizeGitStatus(target.status)}</span>
            <span>{target.subtitle}</span>
            {(target.status.warnings.length > 0 || target.status.errors.length > 0) && (
              <span className="git-warning-text">
                {gitNoticeCounts(target.status).errors} errors · {gitNoticeCounts(target.status).warnings} warnings
              </span>
            )}
          </button>
        ))}
      </div>

      {!loading && targets.length === 0 && (
        <div className="empty-state">
          {scopeSupportsGit
            ? 'No git targets found for this scope.'
            : 'Git, workspace, and terminal snapshots are project/root-path features and are not shown for this non-project space.'}
        </div>
      )}

      {selectedTarget && (
        <div className="git-detail-grid">
          <div className="git-files-panel">
            <div className="git-section-header">
              <div>
                <strong>{selectedTarget.title}</strong>
                <span>{selectedTarget.status.root_path}</span>
              </div>
              <div className="git-meta-list">
                <span>branch {formatBranchLabel(selectedTarget.status)}</span>
                <span>{formatAheadBehind(selectedTarget.status)}</span>
                <span>head {shortSha(selectedTarget.status.head_sha)}</span>
              </div>
            </div>

            {selectedTarget.status.workspace_id && (
              <div className="git-workspace-meta">
                <span>workspace {selectedTarget.status.workspace_id}</span>
                <span>task #{selectedTarget.status.task_id}</span>
                <span>stored branch {selectedTarget.status.workspace_branch}</span>
                <span>base {selectedTarget.status.workspace_base_branch ?? 'unknown'} @ {shortSha(selectedTarget.status.workspace_base_commit)}</span>
                <span>stored head {shortSha(selectedTarget.status.workspace_head_commit)}</span>
              </div>
            )}

            <GitNotices title="Errors" notices={selectedTarget.status.errors} kind="error" />
            <GitNotices title="Warnings" notices={selectedTarget.status.warnings} kind="warning" />

            {selectedGroups.length === 0 ? (
              <div className="empty-state">No changed files reported.</div>
            ) : selectedGroups.map(group => (
              <div key={group.key} className="git-file-group">
                <h4>{group.label} <span>({group.files.length})</span></h4>
                {group.files.map(file => (
                  <button
                    key={`${group.key}:${file.old_path ?? ''}:${file.path}`}
                    className={`git-file-row${selectedFile?.path === file.path ? ' git-file-row-selected' : ''}`}
                    onClick={() => setSelectedFile(file)}
                  >
                    <span className={`git-file-status git-file-status-${file.category}`}>{gitFileStatusLabel(file)}</span>
                    <span className="git-file-path">{file.path}</span>
                    {file.old_path && <span className="git-file-old-path">from {file.old_path}</span>}
                    <span className="git-file-category">{file.category}</span>
                  </button>
                ))}
              </div>
            ))}
          </div>

          <div className="git-diff-panel">
            <div className="git-section-header">
              <div>
                <strong>Diff</strong>
                <span>{selectedFile ? selectedFile.path : 'Select a file'}</span>
              </div>
              {diff && (
                <div className="git-meta-list">
                  {gitDiffBadges(diff).map(badge => (
                    <span key={badge} className={badge === 'truncated' || badge === 'binary' ? 'git-warning-text' : ''}>{badge}</span>
                  ))}
                </div>
              )}
            </div>

            {diffLoading && <div className="empty-state">Loading bounded diff...</div>}
            {diffError && <div className="git-error-text">{diffError}</div>}
            {diff && (
              <>
                <GitNotices title="Diff errors" notices={diff.errors} kind="error" />
                <GitNotices title="Diff warnings" notices={diff.warnings} kind="warning" />
                {diff.diff ? (
                  <pre className="git-diff-text">{diff.diff}</pre>
                ) : (
                  <div className="empty-state">No diff text returned. Untracked or binary files may not have a text diff.</div>
                )}
              </>
            )}
            {!selectedFile && !diffLoading && <div className="empty-state">Select a changed file to inspect its bounded diff.</div>}
          </div>
        </div>
      )}
    </div>
  );
}

async function loadProjectTargets(project: Project, focus?: GitFocus | null): Promise<GitStatusTarget[]> {
  const targets: GitStatusTarget[] = [];
  try {
    const status = await getProjectGitStatus(project.id);
    targets.push({
      id: `project:${project.id}`,
      kind: 'project',
      projectId: project.id,
      title: project.name || project.id,
      subtitle: project.id,
      status,
    });
  } catch (err) {
    targets.push({
      id: `project:${project.id}`,
      kind: 'project',
      projectId: project.id,
      title: project.name || project.id,
      subtitle: project.id,
      status: failedStatus(project.id, err),
    });
  }

  try {
    const workspaces = await listProjectAgentWorkspaces(project.id, {
      taskId: focus?.projectId === project.id ? focus.taskId : undefined,
      limit: 50,
    });
    const workspaceTargets = await Promise.all(workspaces.map(async workspace => {
      try {
        const status = await getWorkspaceGitStatus(project.id, workspace.id);
        return {
          id: `workspace:${project.id}:${workspace.id}`,
          kind: 'workspace' as const,
          projectId: project.id,
          workspaceId: workspace.id,
          title: `${workspace.branch} · #${workspace.task_id}`,
          subtitle: workspace.id,
          status,
        };
      } catch (err) {
        return {
          id: `workspace:${project.id}:${workspace.id}`,
          kind: 'workspace' as const,
          projectId: project.id,
          workspaceId: workspace.id,
          title: `${workspace.branch} · #${workspace.task_id}`,
          subtitle: workspace.id,
          status: failedStatus(project.id, err, workspace.id, workspace.task_id),
        };
      }
    }));
    targets.push(...workspaceTargets);
  } catch {
    // Workspace inspection is additive; a project git status card is still useful if listing workspaces fails.
  }

  return targets;
}

function failedStatus(projectId: string, err: unknown, workspaceId?: string, taskId?: number): GitStatusResponse {
  return {
    project_id: projectId,
    workspace_id: workspaceId ?? null,
    task_id: taskId ?? null,
    workspace_branch: null,
    workspace_base_branch: null,
    workspace_base_commit: null,
    workspace_head_commit: null,
    root_path: '',
    is_git_repository: false,
    branch: null,
    is_detached: false,
    head_sha: null,
    upstream: null,
    ahead: null,
    behind: null,
    dirty_counts: { total: 0, staged: 0, unstaged: 0, untracked: 0, modified: 0, added: 0, deleted: 0, renamed: 0 },
    files: [],
    warnings: [],
    errors: [err instanceof Error ? err.message : String(err)],
    truncated: false,
  };
}

function GitNotices({ title, notices, kind }: { title: string; notices: string[]; kind: 'warning' | 'error' }) {
  if (notices.length === 0) return null;
  return (
    <div className={`git-notices git-notices-${kind}`}>
      <strong>{title}</strong>
      {notices.map((notice, index) => <span key={`${notice}:${index}`}>{notice}</span>)}
    </div>
  );
}
