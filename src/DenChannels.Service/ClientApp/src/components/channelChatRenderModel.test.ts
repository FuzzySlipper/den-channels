import { describe, expect, it } from 'vitest';
import type { ChannelActivityEvent, ChannelMessage } from '../api/types';
import {
  activityMatchesChannelMessage,
  channelMessageDeliveryRequestId,
  findActiveMentionQuery,
  getMentionSuggestions,
  groupActivityEventsForChannelMessages,
  insertMentionToken,
  parseMessageBodySegments,
  sortActivityEvents,
  toActivityDisplayModel,
} from './channelChatRenderModel';

function activity(overrides: Partial<ChannelActivityEvent>): ChannelActivityEvent {
  return {
    id: 1,
    channelId: 10,
    projectId: 'den-channels',
    agentIdentity: 'den-mcp-runner',
    deliveryRequestId: null,
    hermesSessionKey: null,
    displayBlockId: null,
    parentHermesSessionKey: null,
    parentAgentIdentity: null,
    workerRunId: null,
    workerRole: null,
    taskId: null,
    threadId: null,
    anchorMessageId: null,
    eventType: 'tool_call_completed',
    status: 'completed',
    sequence: 1,
    updateVersion: 1,
    title: null,
    summary: null,
    previewJson: null,
    metadataJson: null,
    dedupeKey: null,
    createdAt: '2026-05-19T00:00:00Z',
    updatedAt: '2026-05-19T00:00:00Z',
    ...overrides,
  };
}

function message(overrides: Partial<ChannelMessage>): ChannelMessage {
  return {
    id: 100,
    channelId: 10,
    senderType: 'agent',
    senderIdentity: 'den-mcp-runner',
    body: 'done',
    messageKind: 'agent_text',
    sourceKind: 'gateway_delivery',
    sourceId: 'dr-parent',
    sourceProjectId: 'den-channels',
    summary: null,
    deepLink: null,
    threadRootMessageId: null,
    replyToMessageId: null,
    metadataJson: null,
    deliveryRequestId: 'dr-parent',
    dedupeKey: null,
    createdAt: '2026-05-19T00:01:00Z',
    editedAt: null,
    deletedAt: null,
    ...overrides,
  };
}

describe('parseMessageBodySegments', () => {
  it('turns details/summary artifacts into disclosure segments without raw tag clutter', () => {
    const segments = parseMessageBodySegments('Before\n<details>\n<summary>What I would propose</summary>\n\n1. Take #1308\n2. Store findings\n</details>\nAfter');

    expect(segments).toEqual([
      { type: 'text', text: 'Before\n' },
      { type: 'details', summary: 'What I would propose', body: '1. Take #1308\n2. Store findings' },
      { type: 'text', text: '\nAfter' },
    ]);
  });
});

describe('toActivityDisplayModel', () => {
  it('shows coalesced tool entries such as skill_view den-mcp x2', () => {
    const model = toActivityDisplayModel(activity({
      title: 'skill_view: "den-mcp"',
      metadataJson: JSON.stringify({ count: 2, toolName: 'skill_view: "den-mcp"' }),
      previewJson: JSON.stringify({ preview: 'Loaded den-mcp reference' }),
      taskId: 1528,
    }));

    expect(model.title).toBe('skill_view: "den-mcp"');
    expect(model.count).toBe(2);
    expect(model.preview).toBe('Loaded den-mcp reference');
    expect(model.taskId).toBe(1528);
  });

  it('truncates long terminal previews for compact timeline rows', () => {
    const model = toActivityDisplayModel(activity({
      title: 'terminal',
      previewJson: JSON.stringify({ command: `pytest ${'very-long-output '.repeat(30)}` }),
    }));

    expect(model.preview?.length).toBeLessThanOrEqual(180);
    expect(model.preview?.endsWith('…')).toBe(true);
  });

  it('preserves camelCase display block, worker, and parent fields for spawned worker headers', () => {
    const model = toActivityDisplayModel(activity({
      displayBlockId: 'parent-1567',
      workerRunId: 'coder-1567',
      workerRole: 'coder',
      parentAgentIdentity: 'orchestrator',
      parentHermesSessionKey: 'parent-session-1567',
    }));

    expect(model.displayBlockId).toBe('parent-1567');
    expect(model.workerRunId).toBe('coder-1567');
    expect(model.workerRole).toBe('coder');
    expect(model.parentAgentIdentity).toBe('orchestrator');
    expect(model.parentHermesSessionKey).toBe('parent-session-1567');
    expect('displayDeliveryRequestId' in (model as unknown as Record<string, unknown>)).toBe(false);
  });
});

describe('activity/message grouping', () => {
  it('matches displayBlockId to a final message first-class deliveryRequestId even when child deliveryRequestId differs', () => {
    const parentMessage = message({ id: 42, deliveryRequestId: 'parent-1567' });
    const childEvent = activity({ deliveryRequestId: 'coder-1567', displayBlockId: 'parent-1567', workerRunId: 'coder-1567' });

    expect(activityMatchesChannelMessage(childEvent, parentMessage)).toBe(true);
  });

  it('keeps existing deliveryRequestId matching and legacy fallback matching working', () => {
    const firstClassMessage = message({ id: 42, deliveryRequestId: 'first-class-delivery' });
    const legacyMetadataMessage = message({ id: 43, deliveryRequestId: null, metadataJson: JSON.stringify({ deliveryRequestId: 'metadata-delivery' }) });
    const legacyDedupeMessage = message({ id: 44, deliveryRequestId: null, metadataJson: null, dedupeKey: 'gateway-delivery:dedupe-delivery:final' });

    expect(activityMatchesChannelMessage(activity({ deliveryRequestId: 'first-class-delivery' }), firstClassMessage)).toBe(true);
    expect(activityMatchesChannelMessage(activity({ deliveryRequestId: 'metadata-delivery' }), legacyMetadataMessage)).toBe(true);
    expect(activityMatchesChannelMessage(activity({ displayBlockId: 'dedupe-delivery' }), legacyDedupeMessage)).toBe(true);
    expect(channelMessageDeliveryRequestId(legacyDedupeMessage)).toBe('dedupe-delivery');
  });

  it('matches explicit anchors and final channel message metadata', () => {
    const parentMessage = message({ id: 42, deliveryRequestId: null });

    expect(activityMatchesChannelMessage(activity({ anchorMessageId: 42 }), parentMessage)).toBe(true);
    expect(activityMatchesChannelMessage(activity({ metadataJson: JSON.stringify({ finalChannelMessageId: 42 }) }), parentMessage)).toBe(true);
  });

  it('attaches coder/reviewer displayBlockId activity to an interim block when no final parent message exists', () => {
    const grouped = groupActivityEventsForChannelMessages([], [
      activity({ id: 1, deliveryRequestId: 'coder-1567', displayBlockId: 'parent-1567', workerRunId: 'coder-1567', workerRole: 'coder', createdAt: '2026-05-19T00:00:02Z' }),
      activity({ id: 2, deliveryRequestId: 'reviewer-1567', displayBlockId: 'parent-1567', workerRunId: 'reviewer-1567', workerRole: 'reviewer', createdAt: '2026-05-19T00:00:01Z' }),
      activity({ id: 3 }),
    ]);

    expect(grouped.byMessageId.size).toBe(0);
    expect(grouped.displayBlocks).toHaveLength(1);
    expect(grouped.displayBlocks[0].displayBlockId).toBe('parent-1567');
    expect(grouped.displayBlocks[0].events.map(event => event.id)).toEqual([2, 1]);
    expect(grouped.displayBlocks[0].events.map(event => event.workerRunId)).toEqual(['reviewer-1567', 'coder-1567']);
    expect(grouped.unanchoredEvents.map(event => event.id)).toEqual([3]);
  });

  it('consumes coder/reviewer displayBlockId activity into the final parent delivery message', () => {
    const parentMessage = message({ id: 42, deliveryRequestId: 'parent-1567' });
    const grouped = groupActivityEventsForChannelMessages([parentMessage], [
      activity({ id: 1, deliveryRequestId: 'coder-1567', displayBlockId: 'parent-1567', workerRunId: 'coder-1567', workerRole: 'coder', createdAt: '2026-05-19T00:00:02Z' }),
      activity({ id: 2, deliveryRequestId: 'reviewer-1567', displayBlockId: 'parent-1567', workerRunId: 'reviewer-1567', workerRole: 'reviewer', createdAt: '2026-05-19T00:00:01Z' }),
    ]);

    expect(grouped.byMessageId.get(42)?.map(event => event.id)).toEqual([2, 1]);
    expect(grouped.byMessageId.get(42)?.map(event => event.workerRunId)).toEqual(['reviewer-1567', 'coder-1567']);
    expect(grouped.displayBlocks).toEqual([]);
    expect(grouped.unanchoredEvents).toEqual([]);
  });

  it('orders cross-worker activity by createdAt while sequence remains per workerRunId', () => {
    const sorted = sortActivityEvents([
      activity({ id: 3, workerRunId: 'run-a', sequence: 2, createdAt: '2026-05-19T00:00:01Z' }),
      activity({ id: 2, workerRunId: 'run-b', sequence: 1, createdAt: '2026-05-19T00:00:00Z' }),
      activity({ id: 1, workerRunId: 'run-a', sequence: 1, createdAt: '2026-05-19T00:00:01Z' }),
    ]);

    expect(sorted.map(event => event.id)).toEqual([2, 1, 3]);
  });
});


describe('mention suggestions', () => {
  const members = [
    {
      id: 1,
      memberType: 'user',
      memberIdentity: 'patch',
      membershipStatus: 'active',
      wakePolicy: 'never',
      canSend: true,
      canReact: true,
      canInvite: false,
      cooldownSeconds: 60,
      maxAutoRepliesPerWindow: 1,
      settingsLabel: null,
    },
    {
      id: 2,
      memberType: 'agent',
      memberIdentity: 'den-desktop-runner',
      membershipStatus: 'active',
      wakePolicy: 'mentions_only',
      canSend: true,
      canReact: true,
      canInvite: false,
      cooldownSeconds: 60,
      maxAutoRepliesPerWindow: 1,
      settingsLabel: null,
    },
    {
      id: 3,
      memberType: 'agent',
      memberIdentity: 'muted-agent',
      membershipStatus: 'left',
      wakePolicy: 'mentions_only',
      canSend: true,
      canReact: true,
      canInvite: false,
      cooldownSeconds: 60,
      maxAutoRepliesPerWindow: 1,
      settingsLabel: null,
    },
  ];

  it('filters active members and sorts agents first for @ queries', () => {
    const mention = findActiveMentionQuery('please ask @den');
    expect(mention).toEqual({ start: 11, end: 15, query: 'den' });

    const suggestions = getMentionSuggestions(members, mention?.query ?? '');
    expect(suggestions.map(suggestion => suggestion.identity)).toEqual(['den-desktop-runner']);
    expect(suggestions[0].label).toContain('agent · active · mentions_only');
  });

  it('inserts a stable @memberIdentity token for keyboard selection', () => {
    const mention = findActiveMentionQuery('please ask @de');
    expect(mention).not.toBeNull();
    expect(insertMentionToken('please ask @de', mention!, 'den-desktop-runner')).toBe('please ask @den-desktop-runner ');
  });
});
