import type { AgentWorkspace, GitDiffResponse, GitFileStatus, GitStatusResponse, ReviewRound } from './api/types';

export type GitTargetKind = 'project' | 'workspace';

export interface GitStatusTarget {
  id: string;
  kind: GitTargetKind;
  projectId: string;
  workspaceId?: string;
  title: string;
  subtitle: string;
  status: GitStatusResponse;
}

export interface GitFocus {
  projectId: string;
  taskId?: number;
  workspaceId?: string;
  branch?: string;
}

export interface GitFileGroup {
  key: string;
  label: string;
  files: GitFileStatus[];
}

const GROUP_LABELS: Record<string, string> = {
  staged: 'Staged',
  unstaged: 'Unstaged',
  untracked: 'Untracked',
  renamed: 'Renamed',
  deleted: 'Deleted',
  added: 'Added',
  modified: 'Modified',
  changed: 'Changed',
};

const GROUP_ORDER = ['staged', 'unstaged', 'untracked', 'renamed', 'deleted', 'added', 'modified', 'changed'];

export function shortSha(value: string | null | undefined): string {
  return value ? value.slice(0, 10) : 'unknown';
}

export function formatBranchLabel(status: GitStatusResponse): string {
  if (status.is_detached) return `detached @ ${shortSha(status.head_sha)}`;
  return status.branch || status.workspace_branch || 'unknown branch';
}

export function formatAheadBehind(status: GitStatusResponse): string {
  const ahead = status.ahead ?? 0;
  const behind = status.behind ?? 0;
  if (ahead === 0 && behind === 0) return 'even';
  const parts: string[] = [];
  if (ahead > 0) parts.push(`ahead ${ahead}`);
  if (behind > 0) parts.push(`behind ${behind}`);
  return parts.join(', ');
}

export function dirtyCount(status: GitStatusResponse): number {
  return status.dirty_counts?.total ?? status.files.length;
}

export function summarizeGitStatus(status: GitStatusResponse): string {
  const dirty = dirtyCount(status);
  const branch = formatBranchLabel(status);
  const head = shortSha(status.head_sha);
  const sync = formatAheadBehind(status);
  return `${dirty} dirty · ${branch} · ${sync} · ${head}`;
}

export function buildTaskGitFocus(projectId: string, taskId: number, workspace?: AgentWorkspace | null, branch?: string | null): GitFocus {
  return {
    projectId,
    taskId,
    workspaceId: workspace?.id,
    branch: workspace?.branch ?? branch ?? undefined,
  };
}

export function gitFocusKey(focus: GitFocus | null | undefined): string | null {
  if (!focus) return null;
  return [focus.projectId, focus.taskId ?? '', focus.workspaceId ?? '', focus.branch ?? ''].join(':');
}

export function gitTargetMatchesFocus(target: GitStatusTarget, focus: GitFocus): boolean {
  if (target.projectId !== focus.projectId) return false;
  if (focus.workspaceId && target.workspaceId !== focus.workspaceId) return false;
  if (focus.taskId != null && target.status.task_id != null && target.status.task_id !== focus.taskId) return false;
  if (focus.branch) {
    const branches = [target.status.branch, target.status.workspace_branch, target.title]
      .filter((value): value is string => Boolean(value));
    if (!branches.some(branch => branch.includes(focus.branch!))) return false;
  }
  return true;
}

export function pickFocusedGitTargetId(targets: GitStatusTarget[], focus: GitFocus | null | undefined): string | null {
  if (!focus) return null;
  const workspaceMatch = targets.find(target => target.kind === 'workspace' && gitTargetMatchesFocus(target, focus));
  if (workspaceMatch) return workspaceMatch.id;
  const projectMatch = targets.find(target => target.kind === 'project' && gitTargetMatchesFocus(target, focus));
  return projectMatch?.id ?? null;
}

export function reviewGitAlignmentWarnings(round: ReviewRound | null | undefined, status: GitStatusResponse | null | undefined): string[] {
  if (!round || !status || status.errors.length > 0) return [];
  const warnings: string[] = [];
  const liveBranch = status.branch ?? status.workspace_branch;
  if (liveBranch && round.branch && liveBranch !== round.branch) {
    warnings.push(`Review branch '${round.branch}' differs from live git branch '${liveBranch}'.`);
  }
  if (status.workspace_base_branch && round.base_branch && status.workspace_base_branch !== round.base_branch) {
    warnings.push(`Review base branch '${round.base_branch}' differs from workspace base branch '${status.workspace_base_branch}'.`);
  }
  if (status.workspace_base_commit && round.base_commit && !sameCommit(status.workspace_base_commit, round.base_commit)) {
    warnings.push(`Review base ${shortSha(round.base_commit)} differs from workspace base ${shortSha(status.workspace_base_commit)}.`);
  }
  if (status.head_sha && round.head_commit && !sameCommit(status.head_sha, round.head_commit)) {
    warnings.push(`Review head ${shortSha(round.head_commit)} differs from live git HEAD ${shortSha(status.head_sha)}.`);
  }
  return warnings;
}

export function classifyGitFile(file: GitFileStatus): string {
  if (file.is_untracked) return 'untracked';
  if (file.category === 'renamed' || file.category === 'deleted' || file.category === 'added') return file.category;

  const indexChanged = isChangedStatus(file.index_status);
  const worktreeChanged = isChangedStatus(file.worktree_status);
  if (indexChanged && !worktreeChanged) return 'staged';
  if (worktreeChanged && !indexChanged) return 'unstaged';
  if (file.category && GROUP_LABELS[file.category]) return file.category;
  if (indexChanged) return 'staged';
  if (worktreeChanged) return 'unstaged';
  return 'changed';
}

export function groupGitFiles(files: GitFileStatus[]): GitFileGroup[] {
  const byKey = new Map<string, GitFileStatus[]>();
  for (const file of files) {
    const key = classifyGitFile(file);
    byKey.set(key, [...(byKey.get(key) ?? []), file]);
  }

  return GROUP_ORDER
    .filter(key => (byKey.get(key)?.length ?? 0) > 0)
    .map(key => ({
      key,
      label: GROUP_LABELS[key] ?? key,
      files: [...(byKey.get(key) ?? [])].sort((a, b) => a.path.localeCompare(b.path)),
    }));
}

export function gitFileStatusLabel(file: GitFileStatus): string {
  if (file.is_untracked) return '??';
  const index = normalizeStatus(file.index_status);
  const worktree = normalizeStatus(file.worktree_status);
  return `${index}${worktree}`;
}

export function shouldRequestStagedDiff(file: GitFileStatus): boolean {
  return isChangedStatus(file.index_status) && !isChangedStatus(file.worktree_status);
}

export function sameGitFile(left: GitFileStatus, right: GitFileStatus): boolean {
  return left.path === right.path && (left.old_path ?? null) === (right.old_path ?? null);
}

export function gitNoticeCounts(status: Pick<GitStatusResponse, 'warnings' | 'errors'>): { warnings: number; errors: number } {
  return { warnings: status.warnings.length, errors: status.errors.length };
}

export function gitDiffBadges(diff: GitDiffResponse): string[] {
  const badges: string[] = [];
  if (diff.truncated) badges.push('truncated');
  if (diff.binary) badges.push('binary');
  if (diff.errors.length > 0) badges.push(`${diff.errors.length} error${diff.errors.length === 1 ? '' : 's'}`);
  if (diff.warnings.length > 0) badges.push(`${diff.warnings.length} warning${diff.warnings.length === 1 ? '' : 's'}`);
  badges.push(`max ${diff.max_bytes} bytes`);
  return badges;
}

function normalizeStatus(value: string | null): string {
  return !value || value === '.' ? ' ' : value;
}

function isChangedStatus(value: string | null): boolean {
  return Boolean(value && value !== '.' && value !== '?' && value !== ' ');
}

function sameCommit(left: string, right: string): boolean {
  return left.startsWith(right) || right.startsWith(left);
}
