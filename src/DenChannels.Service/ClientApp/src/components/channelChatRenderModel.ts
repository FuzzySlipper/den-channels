import type { ChannelActivityEvent, ChannelMessage, GatewayMember } from '../api/types';

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
  displayBlockId: string | null;
  workerRunId: string | null;
  workerRole: string | null;
  parentAgentIdentity: string | null;
  parentHermesSessionKey: string | null;
  status: string;
  deliveryStage: string;
  terminal: boolean;
  title: string;
  preview: string | null;
  count: number | null;
  taskId: number | null;
  anchorMessageId: number | null;
  finalChannelMessageId: number | null;
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
    displayBlockId: event.displayBlockId,
    workerRunId: event.workerRunId,
    workerRole: event.workerRole,
    parentAgentIdentity: event.parentAgentIdentity,
    parentHermesSessionKey: event.parentHermesSessionKey,
    status: event.status || event.eventType,
    deliveryStage: event.deliveryStage || 'progress',
    terminal: Boolean(event.terminal),
    title,
    preview: summarizePreview(preview, event.summary),
    count,
    taskId: event.taskId,
    anchorMessageId: event.anchorMessageId,
    finalChannelMessageId: event.finalChannelMessageId,
    createdAt: event.createdAt,
  };
}

export function sortActivityEvents(events: ChannelActivityEvent[]): ChannelActivityEvent[] {
  return [...events].sort((left, right) => {
    const createdAtDiff = Date.parse(left.createdAt) - Date.parse(right.createdAt);
    if (createdAtDiff !== 0 && Number.isFinite(createdAtDiff)) return createdAtDiff;
    if (left.workerRunId && left.workerRunId === right.workerRunId && left.sequence !== right.sequence) {
      return left.sequence - right.sequence;
    }
    return left.id - right.id;
  });
}

export interface ActivityDisplayBlock {
  displayBlockId: string;
  events: ChannelActivityEvent[];
}

export interface GroupedChannelActivityEvents {
  byMessageId: Map<number, ChannelActivityEvent[]>;
  displayBlocks: ActivityDisplayBlock[];
  unanchoredEvents: ChannelActivityEvent[];
}

export function groupActivityEventsForChannelMessages(
  messages: ChannelMessage[],
  events: ChannelActivityEvent[],
): GroupedChannelActivityEvents {
  const byMessageId = new Map<number, ChannelActivityEvent[]>();
  const interimBlocksById = new Map<string, ChannelActivityEvent[]>();
  const unanchoredEvents: ChannelActivityEvent[] = [];

  for (const event of events) {
    const message = messages.find(candidate => activityMatchesChannelMessage(event, candidate));
    if (message) {
      const current = byMessageId.get(message.id) ?? [];
      current.push(event);
      byMessageId.set(message.id, current);
      continue;
    }

    if (event.displayBlockId) {
      const current = interimBlocksById.get(event.displayBlockId) ?? [];
      current.push(event);
      interimBlocksById.set(event.displayBlockId, current);
      continue;
    }

    unanchoredEvents.push(event);
  }

  for (const [messageId, messageEvents] of byMessageId.entries()) {
    byMessageId.set(messageId, sortActivityEvents(messageEvents));
  }

  return {
    byMessageId,
    displayBlocks: [...interimBlocksById.entries()]
      .map(([displayBlockId, blockEvents]) => ({ displayBlockId, events: sortActivityEvents(blockEvents) }))
      .sort((left, right) => compareActivityEventArrays(left.events, right.events) || left.displayBlockId.localeCompare(right.displayBlockId)),
    unanchoredEvents: sortActivityEvents(unanchoredEvents),
  };
}

export function activityMatchesChannelMessage(event: ChannelActivityEvent, message: ChannelMessage): boolean {
  if (event.anchorMessageId === message.id) return true;
  if (activityFinalChannelMessageId(event) === message.id) return true;

  const messageDeliveryRequestId = channelMessageDeliveryRequestId(message);
  if (!messageDeliveryRequestId) return false;

  return event.deliveryRequestId === messageDeliveryRequestId || event.displayBlockId === messageDeliveryRequestId;
}

export function channelMessageDeliveryRequestId(
  message: Pick<ChannelMessage, 'metadataJson' | 'sourceKind' | 'sourceId' | 'dedupeKey'> & Partial<Pick<ChannelMessage, 'deliveryRequestId'>>,
): string | null {
  const firstClass = firstString(message.deliveryRequestId);
  if (firstClass) return firstClass;

  const metadata = parseJsonObject(message.metadataJson);
  const metadataDeliveryRequestId = firstIdentifier(
    metadata.deliveryRequestId,
    metadata.delivery_request_id,
    metadata.channelMessageDeliveryRequestId,
    metadata.channel_message_delivery_request_id,
    metadata.deliveryId,
    metadata.delivery_id,
    metadata.requestId,
    metadata.request_id,
  );
  if (metadataDeliveryRequestId) return metadataDeliveryRequestId;

  const dedupeKey = firstString(message.dedupeKey);
  const dedupeDeliveryRequestId = dedupeKey ? legacyDeliveryRequestIdFromDedupeKey(dedupeKey) : null;
  if (dedupeDeliveryRequestId) return dedupeDeliveryRequestId;

  if (message.sourceKind === 'gateway_delivery' || message.sourceKind === 'external_adapter_message') {
    const sourceId = firstString(message.sourceId);
    if (sourceId) return sourceId;
  }

  return null;
}

export interface MentionSuggestion {
  identity: string;
  label: string;
  memberType: string;
  membershipStatus: string;
  wakePolicy: string;
}

export interface MentionQuery {
  start: number;
  end: number;
  query: string;
}

export function findActiveMentionQuery(draft: string, cursorPosition = draft.length): MentionQuery | null {
  const prefix = draft.slice(0, cursorPosition);
  const match = /(^|\s)@([A-Za-z0-9_.-]*)$/.exec(prefix);
  if (!match || match.index == null) return null;
  const start = match.index + match[1].length;
  return { start, end: cursorPosition, query: match[2] ?? '' };
}

export function getMentionSuggestions(members: GatewayMember[], query: string, limit = 8): MentionSuggestion[] {
  const normalized = query.trim().toLowerCase();
  return members
    .filter(member => member.membershipStatus === 'active')
    .filter(member => normalized.length === 0 || member.memberIdentity.toLowerCase().includes(normalized))
    .sort((left, right) => {
      const leftAgent = left.memberType === 'agent' ? 0 : 1;
      const rightAgent = right.memberType === 'agent' ? 0 : 1;
      return leftAgent - rightAgent || left.memberIdentity.localeCompare(right.memberIdentity);
    })
    .slice(0, limit)
    .map(member => ({
      identity: member.memberIdentity,
      label: `@${member.memberIdentity} · ${member.memberType} · ${member.membershipStatus} · ${member.wakePolicy}`,
      memberType: member.memberType,
      membershipStatus: member.membershipStatus,
      wakePolicy: member.wakePolicy,
    }));
}

export function insertMentionToken(draft: string, mention: MentionQuery, identity: string): string {
  const before = draft.slice(0, mention.start);
  const after = draft.slice(mention.end);
  const spacer = after.startsWith(' ') || after.length === 0 ? '' : ' ';
  return `${before}@${identity} ${spacer}${after}`;
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

function firstIdentifier(...values: unknown[]): string | null {
  for (const value of values) {
    if (typeof value === 'string' && value.trim().length > 0) return value.trim();
    if (typeof value === 'number' && Number.isFinite(value)) return String(value);
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

function firstPositiveInteger(...values: unknown[]): number | null {
  for (const value of values) {
    if (typeof value === 'number' && Number.isInteger(value) && value > 0) return value;
    if (typeof value === 'string') {
      const parsed = Number(value);
      if (Number.isInteger(parsed) && parsed > 0) return parsed;
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

function activityFinalChannelMessageId(event: ChannelActivityEvent): number | null {
  const metadata = parseJsonObject(event.metadataJson);
  return firstPositiveInteger(event.finalChannelMessageId, metadata.finalChannelMessageId, metadata.final_channel_message_id);
}

function legacyDeliveryRequestIdFromDedupeKey(dedupeKey: string): string | null {
  const gatewayDelivery = /^gateway-delivery:(.+):final$/i.exec(dedupeKey);
  if (gatewayDelivery?.[1]) return gatewayDelivery[1];
  const named = /(?:^|[:|;,\s])(?:deliveryRequestId|delivery_request_id|channelMessageDeliveryRequestId|channel_message_delivery_request_id)=([^:|;,\s]+)/i.exec(dedupeKey);
  return named?.[1] ?? null;
}

function compareActivityEventArrays(left: ChannelActivityEvent[], right: ChannelActivityEvent[]): number {
  const leftFirst = left[0];
  const rightFirst = right[0];
  if (!leftFirst || !rightFirst) return left.length - right.length;
  const sorted = sortActivityEvents([leftFirst, rightFirst]);
  return sorted[0] === leftFirst ? -1 : 1;
}
