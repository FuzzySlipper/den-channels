import { describe, expect, it } from 'vitest';
import type { ChannelActivityEvent, ChannelMessage } from '../api/types';
import { activityMatchesChannelMessage, channelMessageDeliveryRequestId, findActiveMentionQuery, getMentionSuggestions, insertMentionToken, parseMessageBodySegments, toActivityDisplayModel } from './channelChatRenderModel';

function activity(overrides: Partial<ChannelActivityEvent>): ChannelActivityEvent {
  return {
    id: 1,
    channelId: 10,
    projectId: 'den-channels',
    agentIdentity: 'den-mcp-runner',
    deliveryRequestId: null,
    hermesSessionKey: null,
    taskId: null,
    threadId: null,
    anchorMessageId: null,
    eventType: 'tool_call_completed',
    status: 'completed',
    deliveryStage: 'progress',
    terminal: false,
    sequence: 1,
    updateVersion: 1,
    title: null,
    summary: null,
    previewJson: null,
    metadataJson: null,
    dedupeKey: null,
    finalChannelMessageId: null,
    createdAt: '2026-05-19T00:00:00Z',
    updatedAt: '2026-05-19T00:00:00Z',
    ...overrides,
  };
}

function channelMessage(overrides: Partial<ChannelMessage>): ChannelMessage {
  const base: ChannelMessage = {
    id: 42,
    channelId: 10,
    senderType: 'agent',
    senderIdentity: 'den-mcp-runner',
    body: 'final answer',
    messageKind: 'agent_text',
    sourceKind: 'gateway_delivery',
    sourceId: '228',
    sourceProjectId: 'den-channels',
    summary: null,
    deepLink: null,
    threadRootMessageId: null,
    replyToMessageId: null,
    metadataJson: null,
    dedupeKey: 'gateway-delivery:228:final',
    finalChannelMessageId: null,
    createdAt: '2026-05-19T00:00:00Z',
    editedAt: null,
    deletedAt: null,
  };
  return { ...base, ...overrides };
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

describe('activity/message delivery matching', () => {
  it('resolves delivery ids from final gateway messages without terminalizing anything in the UI', () => {
    expect(channelMessageDeliveryRequestId(channelMessage({}))).toBe('228');
    expect(channelMessageDeliveryRequestId(channelMessage({
      sourceId: null,
      dedupeKey: null,
      metadataJson: JSON.stringify({ delivery_request_id: 229 }),
    }))).toBe('229');
  });

  it('matches unanchored activity to the visible final message by delivery id', () => {
    const message = channelMessage({ id: 398, sourceId: '228', dedupeKey: 'gateway-delivery:228:final' });

    expect(activityMatchesChannelMessage(activity({ deliveryRequestId: '228', anchorMessageId: null }), message)).toBe(true);
    expect(activityMatchesChannelMessage(activity({ deliveryRequestId: '999', finalChannelMessageId: 398 }), message)).toBe(true);
    expect(activityMatchesChannelMessage(activity({ deliveryRequestId: '999', anchorMessageId: null }), message)).toBe(false);
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
    expect(model.deliveryStage).toBe('progress');
    expect(model.terminal).toBe(false);
  });

  it('surfaces terminal delivery metadata separately from progress rows', () => {
    const model = toActivityDisplayModel(activity({
      status: 'completed',
      deliveryStage: 'final',
      terminal: true,
      finalChannelMessageId: 4242,
    }));

    expect(model.deliveryStage).toBe('final');
    expect(model.terminal).toBe(true);
    expect(model.finalChannelMessageId).toBe(4242);
  });

  it('truncates long terminal previews for compact timeline rows', () => {
    const model = toActivityDisplayModel(activity({
      title: 'terminal',
      previewJson: JSON.stringify({ command: `pytest ${'very-long-output '.repeat(30)}` }),
    }));

    expect(model.preview?.length).toBeLessThanOrEqual(180);
    expect(model.preview?.endsWith('…')).toBe(true);
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
