import type { ChannelActivityEvent } from '../api/types';

export type MessageBodySegment =
  | { type: 'text'; text: string }
  | { type: 'details'; summary: string; body: string };

const detailsPattern = /<details>\s*<summary>([\s\S]*?)<\/summary>\s*([\s\S]*?)<\/details>/gi;

export function parseMessageBodySegments(body: string): MessageBodySegment[] {
  const segments: MessageBodySegment[] = [];
  let lastIndex = 0;
  for (const match of body.matchAll(detailsPattern)) {
    const index = match.index ?? 0;
    if (index > lastIndex) {
      const text = body.slice(lastIndex, index);
      if (text.length > 0) segments.push({ type: 'text', text });
    }
    segments.push({
      type: 'details',
      summary: cleanInlineMarkdownArtifact(match[1] ?? 'Details') || 'Details',
      body: cleanDetailsBody(match[2] ?? ''),
    });
    lastIndex = index + match[0].length;
  }
  if (lastIndex < body.length) {
    const text = body.slice(lastIndex);
    if (text.length > 0) segments.push({ type: 'text', text });
  }
  return segments.length > 0 ? segments : [{ type: 'text', text: body }];
}

function cleanInlineMarkdownArtifact(value: string): string {
  return value.replace(/<\/?summary>/gi, '').replace(/<\/?details>/gi, '').trim();
}

function cleanDetailsBody(value: string): string {
  return value.replace(/^\n+|\n+$/g, '').replace(/<\/?summary>/gi, '').replace(/<\/?details>/gi, '').trim();
}

export interface ActivityDisplayModel {
  id: number;
  agentIdentity: string;
  status: string;
  title: string;
  preview: string | null;
  count: number | null;
  taskId: number | null;
  anchorMessageId: number | null;
  createdAt: string;
}

export function toActivityDisplayModel(event: ChannelActivityEvent): ActivityDisplayModel {
  const metadata = parseJsonObject(event.metadataJson);
  const preview = parseJsonValue(event.previewJson);
  const title = firstString(
    event.title,
    metadata.toolName,
    metadata.tool_name,
    metadata.name,
    humanizeEventType(event.eventType),
  ) ?? humanizeEventType(event.eventType);
  const count = firstNumber(metadata.count, metadata.coalescedCount, metadata.coalesced_count);
  return {
    id: event.id,
    agentIdentity: event.agentIdentity,
    status: event.status || event.eventType,
    title,
    preview: summarizePreview(preview, event.summary),
    count,
    taskId: event.taskId,
    anchorMessageId: event.anchorMessageId,
    createdAt: event.createdAt,
  };
}

export function sortActivityEvents(events: ChannelActivityEvent[]): ChannelActivityEvent[] {
  return [...events].sort((left, right) => left.id - right.id || left.sequence - right.sequence);
}

function parseJsonObject(value: string | null): Record<string, unknown> {
  const parsed = parseJsonValue(value);
  return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed as Record<string, unknown> : {};
}

function parseJsonValue(value: string | null): unknown {
  if (!value) return null;
  try {
    return JSON.parse(value);
  } catch {
    return value;
  }
}

function firstString(...values: unknown[]): string | null {
  for (const value of values) {
    if (typeof value === 'string' && value.trim().length > 0) return value.trim();
  }
  return null;
}

function firstNumber(...values: unknown[]): number | null {
  for (const value of values) {
    if (typeof value === 'number' && Number.isFinite(value) && value > 1) return value;
    if (typeof value === 'string') {
      const parsed = Number(value);
      if (Number.isFinite(parsed) && parsed > 1) return parsed;
    }
  }
  return null;
}

function summarizePreview(preview: unknown, fallback: string | null): string | null {
  const direct = firstString(
    typeof preview === 'string' ? preview : null,
    preview && typeof preview === 'object' && !Array.isArray(preview) ? (preview as Record<string, unknown>).preview : null,
    preview && typeof preview === 'object' && !Array.isArray(preview) ? (preview as Record<string, unknown>).command : null,
    preview && typeof preview === 'object' && !Array.isArray(preview) ? (preview as Record<string, unknown>).result : null,
    fallback,
  );
  if (direct) return truncate(direct);
  if (preview && typeof preview === 'object') return truncate(JSON.stringify(preview));
  return null;
}

function truncate(value: string, max = 180): string {
  const singleLine = value.replace(/\s+/g, ' ').trim();
  return singleLine.length > max ? `${singleLine.slice(0, max - 1)}…` : singleLine;
}

function humanizeEventType(value: string): string {
  return value.replace(/_/g, ' ').replace(/\b\w/g, char => char.toUpperCase());
}
