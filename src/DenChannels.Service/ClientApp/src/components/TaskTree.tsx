import { useState, useMemo, useCallback } from 'react';
import type { TaskSummary, TaskStatus } from '../api/types';

export interface TaskNode {
  task: TaskSummary;
  children: TaskNode[];
}

interface Props {
  tasks: TaskSummary[];
  selectedTaskId: number | null;
  onSelect: (taskId: number) => void;
  statusFilter: string | null;
  sortMode: string;
}

function buildTree(tasks: TaskSummary[]): TaskNode[] {
  const map = new Map<number, TaskNode>();
  const roots: TaskNode[] = [];

  for (const task of tasks) {
    map.set(task.id, { task, children: [] });
  }

  for (const task of tasks) {
    const node = map.get(task.id)!;
    if (task.parent_id && map.has(task.parent_id)) {
      map.get(task.parent_id)!.children.push(node);
    } else {
      roots.push(node);
    }
  }

  return roots;
}

function sortNodes(nodes: TaskNode[], mode: string): TaskNode[] {
  const sorted = [...nodes];
  switch (mode) {
    case 'id':
      sorted.sort((a, b) => a.task.id - b.task.id);
      break;
    case 'status':
      sorted.sort((a, b) => statusOrder(a.task.status) - statusOrder(b.task.status)
        || a.task.priority - b.task.priority
        || a.task.id - b.task.id);
      break;
    case 'title':
      sorted.sort((a, b) => a.task.title.localeCompare(b.task.title));
      break;
    default: // priority
      sorted.sort((a, b) => a.task.priority - b.task.priority || a.task.id - b.task.id);
  }
  for (const node of sorted) {
    node.children = sortNodes(node.children, mode);
  }
  return sorted;
}

const STATUS_ORDER: Record<string, number> = {
  planned: 0, in_progress: 1, review: 2, blocked: 3, done: 4, cancelled: 5,
};

function statusOrder(s: TaskStatus): number {
  return STATUS_ORDER[s] ?? 99;
}

function statusIcon(s: TaskStatus): { icon: string; cls: string } {
  switch (s) {
    case 'planned':     return { icon: '[ ]', cls: 'status-planned' };
    case 'in_progress': return { icon: '[>]', cls: 'status-in_progress' };
    case 'review':      return { icon: '[?]', cls: 'status-review' };
    case 'blocked':     return { icon: '[!]', cls: 'status-blocked' };
    case 'done':        return { icon: '[x]', cls: 'status-done' };
    case 'cancelled':   return { icon: '[-]', cls: 'status-cancelled' };
  }
}

function priorityLabel(p: number): string {
  if (p === 1) return '!!';
  if (p === 2) return '! ';
  return '  ';
}

export function TaskTree({ tasks, selectedTaskId, onSelect, statusFilter, sortMode }: Props) {
  const [expanded, setExpanded] = useState<Set<number>>(new Set());

  const filtered = useMemo(() => {
    if (!statusFilter) return tasks;
    return tasks.filter(t => t.status === statusFilter);
  }, [tasks, statusFilter]);

  const tree = useMemo(() => sortNodes(buildTree(filtered), sortMode), [filtered, sortMode]);

  const toggleExpand = useCallback((id: number, e: React.MouseEvent) => {
    e.stopPropagation();
    setExpanded(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);

  const renderNode = (node: TaskNode, depth: number): React.ReactNode => {
    const { task } = node;
    const hasChildren = node.children.length > 0;
    const isExpanded = expanded.has(task.id);
    const si = statusIcon(task.status);

    return (
      <div key={task.id}>
        <div
          className={`tree-node${task.id === selectedTaskId ? ' selected' : ''}`}
          style={{ paddingLeft: `${10 + depth * 16}px` }}
          onClick={() => onSelect(task.id)}
        >
          {hasChildren ? (
            <span className="tree-toggle" onClick={(e) => toggleExpand(task.id, e)}>
              {isExpanded ? '▾' : '▸'}
            </span>
          ) : (
            <span className="tree-toggle" />
          )}
          <span className={`tree-status-icon ${si.cls}`}>{si.icon}</span>
          <span className={`priority-${task.priority}`}>{priorityLabel(task.priority)}</span>
          <span className="tree-id">#{task.id}</span>
          <span className={`tree-title${task.status === 'cancelled' ? ' status-cancelled' : ''}`}>
            {task.title}
          </span>
          {task.subtask_count > 0 && !isExpanded && (
            <span className="tree-subtask-count">(+{task.subtask_count})</span>
          )}
        </div>
        {isExpanded && node.children.map(child => renderNode(child, depth + 1))}
      </div>
    );
  };

  if (tree.length === 0) {
    return <div className="empty">No tasks</div>;
  }

  return <>{tree.map(node => renderNode(node, 0))}</>;
}
