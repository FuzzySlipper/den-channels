import { describe, expect, it } from 'vitest';
import type { ChannelActivityEvent } from '../api/types';
import { findActiveMentionQuery, getMentionSuggestions, insertMentionToken, parseMessageBodySegments, toActivityDisplayModel } from './channelChatRenderModel';

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
