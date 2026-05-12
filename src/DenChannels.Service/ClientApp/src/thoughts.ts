import type { AgentStreamEntry, SubagentRunDetail, SubagentRunSummary, SubagentRunWorkEvent } from './api/types';

export type ThoughtKind = 'assistant_message' | 'reasoning';
export type ThoughtSource = 'parent_agent' | 'subagent_stream' | 'subagent_run';

export interface ThoughtFeedItem {
  id: string;
  kind: ThoughtKind;
  source: ThoughtSource;
  eventType: string;
  createdAt: string;
  timestampMs: number;
  projectId: string | null;
  taskId: number | null;
  agent: string | null;
  role: string | null;
  runId: string | null;
  model: string | null;
  textPreview: string | null;
  reasoningChars: number | null;
  reasoningRedacted: boolean | null;
  reasoningSummaryPreview: string | null;
  rawPreviewAvailable: boolean;
  streamEntry?: AgentStreamEntry;
  run?: SubagentRunSummary;
  workEvent?: SubagentRunWorkEvent;
}

export interface ThoughtFilters {
  project?: string;
  taskId?: number;
  agent?: string;
  role?: string;
}

type ThoughtContext = {
  idPrefix: string;
  source: ThoughtSource;
  fallbackCreatedAt: string;
  projectId?: string | null;
  taskId?: number | null;
  agent?: string | null;
  role?: string | null;
  runId?: string | null;
  streamEntry?: AgentStreamEntry;
  run?: SubagentRunSummary;
  index?: number;
};

export function thoughtItemFromStreamEntry(entry: AgentStreamEntry): ThoughtFeedItem | null {
  const metadata = recordValue(entry.metadata);
  const metadataEvent = recordValue(metadata?.event);
  const eventType = stringValue(metadataEvent?.type) ?? streamEventTypeToWorkEventType(entry.event_type);
  if (!eventType) return null;

  const event: SubagentRunWorkEvent = {
    ...(metadataEvent ?? {}),
    type: eventType,
  } as SubagentRunWorkEvent;

  const hasMetadataToolCalls = Array.isArray(metadataEvent?.tool_calls) && metadataEvent.tool_calls.length > 0;
  if (!event.text_preview && entry.body && isThoughtWorkEventType(eventType) && !hasMetadataToolCalls) {
    event.text_preview = entry.body;
  }

  return thoughtItemFromWorkEvent(event, {
    idPrefix: `stream:${entry.id}`,
    source: eventType.startsWith('agent.work_') ? 'parent_agent' : 'subagent_stream',
    fallbackCreatedAt: entry.created_at,
    projectId: entry.project_id,
    taskId: entry.task_id,
    agent: entry.sender,
    role: stringValue(metadata?.role),
    runId: stringValue(metadata?.run_id),
    streamEntry: entry,
  });
}

export function thoughtItemsFromSubagentRunDetail(detail: SubagentRunDetail): ThoughtFeedItem[] {
  const items = detail.work_events
    .map((workEvent, index) => thoughtItemFromWorkEvent(workEvent, {
      idPrefix: `run:${detail.summary.run_id}`,
      source: 'subagent_run',
      fallbackCreatedAt: detail.summary.latest.created_at,
      projectId: detail.summary.project_id,
      taskId: detail.summary.task_id,
      agent: detail.summary.latest.sender,
      role: detail.summary.role,
      runId: detail.summary.run_id,
      run: detail.summary,
      index,
    }))
    .filter((item): item is ThoughtFeedItem => item !== null);
  return coalesceSubagentRunThoughts(items);
}

export function sortThoughtItems(items: ThoughtFeedItem[]): ThoughtFeedItem[] {
  return [...items].sort((left, right) => right.timestampMs - left.timestampMs || right.id.localeCompare(left.id));
}

export function filterThoughtItems(items: ThoughtFeedItem[], filters: ThoughtFilters): ThoughtFeedItem[] {
  const project = filters.project?.trim().toLowerCase();
  const agent = filters.agent?.trim().toLowerCase();
  const role = filters.role?.trim().toLowerCase();
  return items.filter(item => {
    if (project && !(item.projectId ?? '').toLowerCase().includes(project)) return false;
    if (filters.taskId != null && item.taskId !== filters.taskId) return false;
    if (agent && ![item.agent, item.streamEntry?.sender].some(value => includesLower(value, agent))) return false;
    if (role && !includesLower(item.role, role)) return false;
    return true;
  });
}

export function hasRawReasoningPreview(items: ThoughtFeedItem[]): boolean {
  return items.some(item => item.kind === 'reasoning' && item.rawPreviewAvailable);
}

export function thoughtKindLabel(kind: ThoughtKind): string {
  return kind === 'assistant_message' ? 'assistant' : 'reasoning';
}

export function thoughtSourceLabel(source: ThoughtSource): string {
  switch (source) {
    case 'parent_agent': return 'parent';
    case 'subagent_stream': return 'subagent stream';
    case 'subagent_run': return 'subagent run';
  }
}

export function summarizeThoughtItem(item: ThoughtFeedItem, showRawReasoning: boolean): string {
  if (item.kind === 'assistant_message') {
    return item.textPreview?.trim() || 'Assistant message activity.';
  }

  if (showRawReasoning && item.rawPreviewAvailable && item.textPreview) {
    return item.textPreview;
  }

  if (item.reasoningSummaryPreview) {
    return item.reasoningSummaryPreview;
  }

  const chars = item.reasoningChars != null ? `${item.reasoningChars} chars` : 'reasoning activity';
  const redaction = item.reasoningRedacted === false ? 'raw preview hidden' : 'redacted';
  return `${chars}, ${redaction}.`;
}

function coalesceSubagentRunThoughts(items: ThoughtFeedItem[]): ThoughtFeedItem[] {
  const latestReasoningUpdate = items
    .filter(item => item.kind === 'reasoning' && item.eventType.endsWith('_update'))
    .sort((left, right) => right.timestampMs - left.timestampMs)[0];

  return items.filter(item => {
    if (item.kind === 'assistant_message') return item.eventType.endsWith('_end');
    if (item.kind === 'reasoning' && item.eventType.endsWith('_update')) return item === latestReasoningUpdate;
    return true;
  });
}

function thoughtItemFromWorkEvent(workEvent: SubagentRunWorkEvent, context: ThoughtContext): ThoughtFeedItem | null {
  const type = stringValue(workEvent.type);
  if (!type) return null;

  const kind = thoughtKindForWorkEventType(type);
  if (!kind) return null;

  const textPreview = stringValue(workEvent.text_preview);
  if (kind === 'assistant_message' && !textPreview) return null;

  const timestampMs = numberValue(workEvent.ts) ?? parseCreatedAt(context.fallbackCreatedAt);
  const createdAt = numberValue(workEvent.ts) != null ? formatServerIso(timestampMs) : context.fallbackCreatedAt;
  const role = stringValue(workEvent.agent_role)
    ?? stringValue(workEvent.subagent_role)
    ?? context.role
    ?? null;
  const agent = stringValue(workEvent.agent) ?? context.agent ?? null;
  const runId = stringValue(workEvent.run_id) ?? context.runId ?? null;
  const projectId = stringValue(workEvent.project_id) ?? context.projectId ?? null;
  const taskId = numberValue(workEvent.task_id) ?? context.taskId ?? null;
  const model = stringValue(workEvent.model) ?? stringValue(workEvent.requested_model) ?? null;
  const reasoningRedacted = booleanValue(workEvent.reasoning_redacted);
  const reasoningSummaryPreview = stringValue(workEvent.reasoning_summary_preview);
  const rawPreviewAvailable = kind === 'reasoning' && reasoningRedacted === false && Boolean(textPreview);

  return {
    id: [context.idPrefix, context.index ?? createdAt, type].join(':'),
    kind,
    source: context.source,
    eventType: type,
    createdAt,
    timestampMs,
    projectId,
    taskId,
    agent,
    role,
    runId,
    model,
    textPreview: textPreview ?? null,
    reasoningChars: numberValue(workEvent.reasoning_chars) ?? numberValue(workEvent.thinking_chars) ?? null,
    reasoningRedacted: reasoningRedacted ?? null,
    reasoningSummaryPreview: reasoningSummaryPreview ?? null,
    rawPreviewAvailable,
    streamEntry: context.streamEntry,
    run: context.run,
    workEvent,
  };
}

function thoughtKindForWorkEventType(type: string): ThoughtKind | null {
  if (type.includes('.work_reasoning_') || type.includes('_work_reasoning_')) return 'reasoning';
  if (type.includes('.work_message_') || type.includes('_work_message_')) return 'assistant_message';
  return null;
}

function isThoughtWorkEventType(type: string): boolean {
  return thoughtKindForWorkEventType(type) !== null;
}

function streamEventTypeToWorkEventType(eventType: string): string | null {
  if (eventType.startsWith('agent_work_')) return eventType.replace(/^agent_work_/, 'agent.work_');
  if (eventType.startsWith('subagent_work_')) return eventType.replace(/^subagent_work_/, 'subagent.work_');
  return null;
}

function recordValue(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null;
}

function stringValue(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim().length > 0 ? value : undefined;
}

function numberValue(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function booleanValue(value: unknown): boolean | undefined {
  return typeof value === 'boolean' ? value : undefined;
}

function includesLower(value: string | null | undefined, needle: string): boolean {
  return Boolean(value?.toLowerCase().includes(needle));
}

function parseCreatedAt(value: string): number {
  const normalized = value.endsWith('Z') ? value : `${value}Z`;
  const parsed = Date.parse(normalized);
  return Number.isFinite(parsed) ? parsed : 0;
}

function formatServerIso(timestampMs: number): string {
  return new Date(timestampMs).toISOString().replace(/\.\d{3}Z$/, '');
}
