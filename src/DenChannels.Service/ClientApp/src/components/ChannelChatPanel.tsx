import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ChangeEvent, FormEvent, KeyboardEvent, UIEvent } from 'react';
import type { Channel, ChannelActivityEvent, ChannelMessage, ChannelReactionSummary, GatewayDirectAgentMessage, GatewayMember, GatewayMemberships, GatewayTestWake } from '../api/types';
import {
  ensureAgentCommonsChannel,
  ensureProjectDefaultChannel,
  listChannelActivityEvents,
  listChannelMessages,
  listChannelReactions,
  listChannels,
  listGatewayMemberships,
  postChannelMessage,
  addChannelReaction,
  postGatewayDirectAgentMessage,
  postGatewayTestWake,
  upsertChannelMembership,
} from '../api/client';
import { usePolling } from '../hooks/usePolling';
import { formatTimeAgo } from '../utils';
import { findActiveMentionQuery, getMentionSuggestions, groupActivityEventsForChannelMessages, insertMentionToken, parseMessageBodySegments, sortActivityEvents, toActivityDisplayModel } from './channelChatRenderModel';

const SENDER_IDENTITY_STORAGE_KEY = 'den-channel-sender-identity';
const DEFAULT_WAKE_POLICY = 'mentions_only';

const WAKE_POLICY_OPTIONS = [
  { value: 'never', label: 'never' },
  { value: 'mentions_only', label: 'mentions only' },
  { value: 'direct_questions_only', label: 'direct questions' },
  { value: 'substantive_digest', label: 'substantive digest' },
  { value: 'all_human_messages', label: 'all human' },
  { value: 'all_messages_except_self', label: 'all except self' },
];

const MEMBERSHIP_STATUS_OPTIONS = [
  { value: 'active', label: 'active' },
  { value: 'muted', label: 'muted' },
  { value: 'left', label: 'left' },
  { value: 'banned', label: 'banned' },
];

const QUICK_REACTIONS = ['✅', '👀', '👍', '🫡', '❓'];
const SCROLL_BOTTOM_PIN_THRESHOLD_PX = 48;

interface Props {
  projectId: string | null;
  spaceName?: string | null;
  panelSize: ChannelChatPanelSize;
  scrollResetKey?: string | null;
  onPanelSizeChange: (size: ChannelChatPanelSize) => void;
}

export type ChannelChatPanelSize = 'small' | 'medium' | 'large';
type ChannelSendMode = 'channel' | 'direct';

interface WakeProgress {
  label: string;
  detail: string;
  state: 'recorded' | 'preparing' | 'replied';
}

const PANEL_SIZE_OPTIONS: Array<{ value: ChannelChatPanelSize; label: string }> = [
  { value: 'small', label: '25%' },
  { value: 'medium', label: '50%' },
  { value: 'large', label: '80%' },
];

function channelLabel(channel: Channel | null, projectId: string | null): string {
  if (channel?.slug === 'agent-commons') return '#agent-commons';
  if (channel) return `#${channel.slug}`;
  if (projectId) return `#project-${projectId}`;
  return '#agent-commons';
}

function isScrollElementPinnedToBottom(element: HTMLElement): boolean {
  return element.scrollHeight - element.scrollTop - element.clientHeight <= SCROLL_BOTTOM_PIN_THRESHOLD_PX;
}

async function resolveAgentCommonsChannel(): Promise<Channel> {
  const systemChannels = await listChannels({ kind: 'system', limit: 100 });
  const existing = systemChannels.find(channel => channel.slug === 'agent-commons');
  return existing ?? ensureAgentCommonsChannel();
}

function preferredDefaultChannel(channels: Channel[], projectId: string | null): Channel | undefined {
  if (!projectId) {
    return channels.find(candidate => candidate.slug === 'agent-commons') ?? channels[0];
  }
  return channels.find(candidate => candidate.kind === 'project_default') ?? channels[0];
}

function messageSender(message: ChannelMessage): string {
  return message.senderIdentity || message.senderType;
}

function readStoredSenderIdentity(): string {
  if (typeof window === 'undefined') return '';
  try {
    return window.localStorage.getItem(SENDER_IDENTITY_STORAGE_KEY)?.trim() ?? '';
  } catch {
    return '';
  }
}

function persistSenderIdentity(identity: string): void {
  if (typeof window === 'undefined') return;
  try {
    const normalized = identity.trim();
    if (normalized) {
      window.localStorage.setItem(SENDER_IDENTITY_STORAGE_KEY, normalized);
    } else {
      window.localStorage.removeItem(SENDER_IDENTITY_STORAGE_KEY);
    }
  } catch {
    // localStorage can be unavailable in private/embedded contexts; the in-memory
    // state still provides the identity seam for this session.
  }
}

function memberStatus(member: GatewayMember): string {
  return [member.membershipStatus, member.wakePolicy, member.settingsLabel]
    .filter(Boolean)
    .join(' · ');
}

function parseDirectMessageMetadata(message: ChannelMessage): Record<string, unknown> | null {
  if (message.sourceKind !== 'wake_event' || !message.metadataJson) return null;
  try {
    const parsed = JSON.parse(message.metadataJson) as Record<string, unknown>;
    return parsed.deliveryMode === 'direct_agent_message' ? parsed : null;
  } catch {
    return null;
  }
}

function directMessageEvidence(message: ChannelMessage): { status: string; target: string | null; url: string } | null {
  const metadata = parseDirectMessageMetadata(message);
  if (!metadata) return null;
  const status = [metadata.deliveryStatus, metadata.claimStatus, metadata.completionStatus, metadata.suppressionStatus]
    .filter(value => typeof value === 'string' && value.length > 0)
    .join(' · ');
  const target = typeof metadata.targetMemberIdentity === 'string' ? metadata.targetMemberIdentity : null;
  return {
    status: status || 'recorded_pending_claim',
    target,
    url: `/api/gateway/messages/${message.id}`,
  };
}

function findAgentReplyForMessage(message: ChannelMessage, messages: ChannelMessage[], agentIdentity?: string): ChannelMessage | null {
  const directMetadata = parseDirectMessageMetadata(message);
  const target = agentIdentity ?? (typeof directMetadata?.targetMemberIdentity === 'string' ? directMetadata.targetMemberIdentity : '');
  if (!target) return null;
  const expectedDedupeKey = `channel-message:${message.id}:agent:${target}`;
  return messages.find(candidate => {
    if (candidate.senderType !== 'agent') return false;
    if (candidate.id <= message.id) return false;
    if (candidate.dedupeKey === expectedDedupeKey) return true;
    if (
      (candidate.sourceKind === 'gateway_delivery' || candidate.sourceKind === 'external_adapter_message')
      && candidate.senderIdentity === target
      && candidate.body.includes(`message/${message.id}`)
    ) return true;
    return false;
  }) ?? null;
}

function deriveWakeProgress(message: ChannelMessage, messages: ChannelMessage[]): WakeProgress | null {
  const evidence = directMessageEvidence(message);
  if (!evidence) return null;
  const reply = findAgentReplyForMessage(message, messages);
  if (reply) {
    return {
      label: 'Reply posted',
      detail: `Agent response #${reply.id} is visible in the channel.`,
      state: 'replied',
    };
  }

  const normalizedStatus = evidence.status.toLowerCase();
  const statusParts = normalizedStatus.split(' · ').map(part => part.trim());
  const hasClaimOrDeliveryEvidence = statusParts.some(part => part === 'claimed' || part === 'delivered' || part === 'delivering');
  if (hasClaimOrDeliveryEvidence) {
    return {
      label: 'Agent is preparing a reply',
      detail: evidence.status,
      state: 'preparing',
    };
  }

  return {
    label: 'Agent wake recorded',
    detail: evidence.status,
    state: 'recorded',
  };
}

function participantShouldWakeForMessage(member: GatewayMember, message: ChannelMessage): boolean {
  if (message.senderType !== 'user' || message.messageKind !== 'human_text') return false;
  const directMetadata = parseDirectMessageMetadata(message);
  if (directMetadata) {
    return directMetadata.targetMemberIdentity === member.memberIdentity;
  }

  const body = message.body.toLowerCase();
  const mention = `@${member.memberIdentity.toLowerCase()}`;
  switch (member.wakePolicy) {
    case 'all_human_messages':
      return true;
    case 'all_messages_except_self':
      return message.senderIdentity !== member.memberIdentity;
    case 'mentions_only':
      return body.includes(mention);
    case 'direct_questions_only':
      return body.includes(mention) && body.includes('?');
    default:
      return false;
  }
}

function deriveParticipantActivity(member: GatewayMember, messages: ChannelMessage[]): 'active' | 'working' {
  if (!memberIsActiveAgent(member)) return 'active';
  for (const message of [...messages].reverse()) {
    if (message.senderType === 'agent' && message.senderIdentity === member.memberIdentity) {
      return 'active';
    }
    if (!participantShouldWakeForMessage(member, message)) continue;
    return findAgentReplyForMessage(message, messages, member.memberIdentity) ? 'active' : 'working';
  }
  return 'active';
}

function memberIsActiveAgent(member: GatewayMember): boolean {
  return member.memberType === 'agent' && member.membershipStatus === 'active';
}

function MessageBody({ body }: { body: string }) {
  return (
    <>
      {parseMessageBodySegments(body).map((segment, index) => segment.type === 'details' ? (
        <details key={`details-${index}`} className="channel-chat-details-block">
          <summary>{segment.summary}</summary>
          <div>{segment.body}</div>
        </details>
      ) : (
        <span key={`text-${index}`}>{segment.text}</span>
      ))}
    </>
  );
}

function ActivityTimeline({ events, compact = false }: { events: ChannelActivityEvent[]; compact?: boolean }) {
  if (events.length === 0) return null;
  const displayEvents = sortActivityEvents(events).map(toActivityDisplayModel);
  return (
    <div className={`channel-chat-activity-timeline ${compact ? 'channel-chat-activity-timeline-compact' : ''}`} aria-label="Agent activity breadcrumbs">
      {!compact && (
        <div className="channel-chat-activity-heading">
          <span>Agent activity</span>
          <span>{displayEvents.length} breadcrumb{displayEvents.length === 1 ? '' : 's'}</span>
        </div>
      )}
      {displayEvents.map(event => (
        <div key={event.id} className={`channel-chat-activity-row channel-chat-activity-${activityStatusClass(event.status)} ${event.terminal ? 'channel-chat-activity-terminal' : ''}`}>
          <span className="message-time">{formatTimeAgo(event.createdAt)}</span>
          <span className="channel-chat-activity-agent">{event.agentIdentity}</span>
          <span className="channel-chat-activity-main">
            {activityWorkerChip(event)}
            <strong>{event.title}{event.count ? ` ×${event.count}` : ''}</strong>
            <span className="channel-chat-activity-status">{event.status}</span>
            <span className="channel-chat-activity-stage">{event.terminal ? 'terminal' : event.deliveryStage}</span>
            {event.taskId && <span className="channel-chat-activity-task">task #{event.taskId}</span>}
            {event.finalChannelMessageId && <span className="channel-chat-activity-task">final message #{event.finalChannelMessageId}</span>}
            {event.preview && <span className="channel-chat-activity-preview">{event.preview}</span>}
          </span>
        </div>
      ))}
    </div>
  );
}

function DeliveryProgressCards({ blocks }: { blocks: Array<{ displayBlockId: string; events: ChannelActivityEvent[] }> }) {
  if (blocks.length === 0) return null;
  return (
    <div className="channel-chat-delivery-progress-list" aria-label="Agent delivery progress">
      {blocks.map(block => {
        const group = sortActivityEvents(block.events);
        const terminal = group.some(event => event.terminal);
        const latest = group[group.length - 1];
        const model = latest ? toActivityDisplayModel(latest) : null;
        return (
          <details key={block.displayBlockId} className={`channel-chat-delivery-progress ${terminal ? 'channel-chat-delivery-progress-terminal' : ''}`} open={!terminal}>
            <summary>
              <span>{terminal ? 'Delivery finished' : 'Agent working'}</span>
              {latest && <strong>{latest.agentIdentity}</strong>}
              <span>{block.displayBlockId}</span>
              <span>{group.length} event{group.length === 1 ? '' : 's'}</span>
              {model?.finalChannelMessageId && <span>final message #{model.finalChannelMessageId}</span>}
            </summary>
            <ActivityTimeline events={group} compact />
          </details>
        );
      })}
    </div>
  );
}

function activityWorkerChip(event: ReturnType<typeof toActivityDisplayModel>) {
  if (!event.workerRunId && !event.workerRole && !event.parentAgentIdentity && !event.parentHermesSessionKey) return null;
  const role = event.workerRole?.trim() || 'worker';
  const run = event.workerRunId ? shortWorkerRunId(event.workerRunId) : 'pending';
  const title = [
    event.displayBlockId ? `display block ${event.displayBlockId}` : null,
    event.parentAgentIdentity ? `parent agent ${event.parentAgentIdentity}` : null,
    event.parentHermesSessionKey ? `parent session ${event.parentHermesSessionKey}` : null,
  ].filter(Boolean).join(' · ');
  return (
    <span className="channel-chat-activity-worker-chip" title={title || undefined}>
      {role} · {event.agentIdentity || 'agent'} · {run}
    </span>
  );
}

function shortWorkerRunId(value: string): string {
  const normalized = value.trim();
  if (normalized.length <= 12) return normalized;
  return normalized.slice(0, 8);
}

function activityStatusClass(status: string): string {
  return status.toLowerCase().replace(/[^a-z0-9_-]+/g, '-');
}

export function ChannelChatPanel({ projectId, spaceName, panelSize, scrollResetKey, onPanelSizeChange }: Props) {
  const [draft, setDraft] = useState('');
  const [mentionActiveIndex, setMentionActiveIndex] = useState(0);
  const [senderIdentity, setSenderIdentity] = useState(readStoredSenderIdentity);
  const [selectedChannelId, setSelectedChannelId] = useState<number | null>(null);
  const [sendMode, setSendMode] = useState<ChannelSendMode>('channel');
  const [autoScroll, setAutoScroll] = useState(true);
  const scrollbackRef = useRef<HTMLDivElement | null>(null);
  const scrollAnchorRef = useRef<HTMLDivElement | null>(null);
  const isScrollPinnedToBottomRef = useRef(true);
  const previousAutoScrollKeyRef = useRef<string | null>(null);
  const pendingAutoScrollSnapKeyRef = useRef<string | null>(null);
  const previousProjectIdRef = useRef<string | null>(projectId);
  const pendingProjectDefaultSelectionRef = useRef<string | null>(null);
  const [targetMemberIdentity, setTargetMemberIdentity] = useState('');
  const [inviteIdentity, setInviteIdentity] = useState('');
  const [inviteWakePolicy, setInviteWakePolicy] = useState(DEFAULT_WAKE_POLICY);
  const [editingMemberIdentity, setEditingMemberIdentity] = useState<string | null>(null);
  const [editingWakePolicy, setEditingWakePolicy] = useState(DEFAULT_WAKE_POLICY);
  const [editingMembershipStatus, setEditingMembershipStatus] = useState('active');
  const [sending, setSending] = useState(false);
  const [inviteSending, setInviteSending] = useState(false);
  const [memberSaving, setMemberSaving] = useState(false);
  const [wakeSending, setWakeSending] = useState(false);
  const [sendError, setSendError] = useState<Error | null>(null);
  const [lastWakeResult, setLastWakeResult] = useState<GatewayTestWake | null>(null);
  const [lastDirectResult, setLastDirectResult] = useState<GatewayDirectAgentMessage | null>(null);
  const normalizedSenderIdentity = senderIdentity.trim();

  const fetchChannels = useCallback(async () => {
    const agentCommons = await resolveAgentCommonsChannel();
    if (!projectId) return [agentCommons];
    const projectChannels = await listChannels({ projectId, limit: 100 });
    if (projectChannels.length > 0) return [...projectChannels, agentCommons];
    const ensured = await ensureProjectDefaultChannel(projectId, {
      displayName: spaceName?.trim() || projectId,
      createdBy: normalizedSenderIdentity || 'den-web',
    });
    return [ensured, agentCommons];
  }, [normalizedSenderIdentity, projectId, spaceName]);

  const {
    data: channels,
    loading: channelLoading,
    error: channelError,
    refresh: refreshChannels,
  } = usePolling<Channel[]>(fetchChannels, 15000);

  const availableChannels = useMemo(
    () => (channels ?? []).filter(candidate => candidate.projectId === projectId || candidate.slug === 'agent-commons'),
    [channels, projectId],
  );

  useEffect(() => {
    if (availableChannels.length === 0) {
      setSelectedChannelId(null);
      previousProjectIdRef.current = projectId;
      pendingProjectDefaultSelectionRef.current = projectId;
      return;
    }

    const projectDefaultChannel = projectId
      ? availableChannels.find(candidate => candidate.kind === 'project_default')
      : undefined;
    const projectChanged = previousProjectIdRef.current !== projectId;
    previousProjectIdRef.current = projectId;

    if (projectChanged) {
      if (!projectId) {
        pendingProjectDefaultSelectionRef.current = null;
        setSelectedChannelId(preferredDefaultChannel(availableChannels, projectId)?.id ?? null);
        return;
      }

      pendingProjectDefaultSelectionRef.current = projectId;
      if (projectDefaultChannel) {
        pendingProjectDefaultSelectionRef.current = null;
        setSelectedChannelId(projectDefaultChannel.id);
      } else if (!selectedChannelId || !availableChannels.some(candidate => candidate.id === selectedChannelId)) {
        setSelectedChannelId(preferredDefaultChannel(availableChannels, projectId)?.id ?? null);
      }
      return;
    }

    if (projectId && pendingProjectDefaultSelectionRef.current === projectId && projectDefaultChannel) {
      pendingProjectDefaultSelectionRef.current = null;
      setSelectedChannelId(projectDefaultChannel.id);
      return;
    }

    if (!selectedChannelId || !availableChannels.some(candidate => candidate.id === selectedChannelId)) {
      setSelectedChannelId(preferredDefaultChannel(availableChannels, projectId)?.id ?? null);
    }
  }, [availableChannels, projectId, selectedChannelId]);

  const activeChannel = useMemo(
    () => availableChannels.find(candidate => candidate.id === selectedChannelId) ?? null,
    [availableChannels, selectedChannelId],
  );

  const fetchMessages = useCallback(
    () => activeChannel ? listChannelMessages(activeChannel.id, { limit: 80 }) : Promise.resolve([]),
    [activeChannel],
  );
  const {
    data: messages,
    loading: messagesLoading,
    error: messagesError,
    refresh: refreshMessages,
  } = usePolling(fetchMessages, 4000);

  const fetchActivityEvents = useCallback(
    () => activeChannel ? listChannelActivityEvents(activeChannel.id, { limit: 120 }) : Promise.resolve([]),
    [activeChannel],
  );
  const {
    data: activityEvents,
    loading: activityLoading,
    error: activityError,
    refresh: refreshActivityEvents,
  } = usePolling<ChannelActivityEvent[]>(fetchActivityEvents, 4000);

  const fetchReactions = useCallback(
    () => activeChannel ? listChannelReactions(activeChannel.id) : Promise.resolve([]),
    [activeChannel],
  );
  const {
    data: reactions,
    refresh: refreshReactions,
  } = usePolling<ChannelReactionSummary[]>(fetchReactions, 5000);

  const fetchMemberships = useCallback(
    () => activeChannel ? listGatewayMemberships({ channelId: activeChannel.id }) : Promise.resolve(null),
    [activeChannel],
  );
  const {
    data: memberships,
    loading: membershipsLoading,
    error: membershipsError,
    refresh: refreshMemberships,
  } = usePolling<GatewayMemberships | null>(fetchMemberships, 5000);

  const sortedMessages = useMemo(() => {
    const visibleMessages = activeChannel
      ? (messages ?? []).filter(message => message.channelId === activeChannel.id)
      : [];
    return [...visibleMessages].sort((left, right) => left.id - right.id);
  }, [activeChannel, messages]);

  const reactionsByMessageId = useMemo(() => {
    const grouped = new Map<number, ChannelReactionSummary[]>();
    for (const reaction of reactions ?? []) {
      const current = grouped.get(reaction.channelMessageId) ?? [];
      current.push(reaction);
      grouped.set(reaction.channelMessageId, current);
    }
    return grouped;
  }, [reactions]);

  const groupedActivityEvents = useMemo(
    () => groupActivityEventsForChannelMessages(sortedMessages, activityEvents ?? []),
    [activityEvents, sortedMessages],
  );
  const activityEventsByMessageId = groupedActivityEvents.byMessageId;
  const deliveryProgressBlocks = groupedActivityEvents.displayBlocks;
  const unanchoredActivityEvents = groupedActivityEvents.unanchoredEvents;

  const members = useMemo(() => memberships?.members ?? [], [memberships]);
  const memberActivityByIdentity = useMemo(() => {
    const entries = members.map(member => [member.memberIdentity, deriveParticipantActivity(member, sortedMessages)] as const);
    return new Map(entries);
  }, [members, sortedMessages]);
  const activeAgentMembers = members.filter(memberIsActiveAgent);
  const selectedTarget = activeAgentMembers.find(member => member.memberIdentity === targetMemberIdentity) ?? null;
  const editingMember = members.find(member => member.memberIdentity === editingMemberIdentity && member.memberType === 'agent') ?? null;
  const inviteExistingMember = members.find(member => member.memberType === 'agent' && member.memberIdentity === inviteIdentity.trim()) ?? null;
  const mentionQuery = useMemo(() => findActiveMentionQuery(draft), [draft]);
  const mentionSuggestions = useMemo(
    () => mentionQuery ? getMentionSuggestions(members, mentionQuery.query) : [],
    [members, mentionQuery],
  );

  useEffect(() => {
    setMentionActiveIndex(0);
  }, [mentionQuery?.query, mentionSuggestions.length]);

  useEffect(() => {
    if (activeAgentMembers.length === 0) {
      setTargetMemberIdentity('');
      return;
    }
    if (!targetMemberIdentity || !activeAgentMembers.some(member => member.memberIdentity === targetMemberIdentity)) {
      setTargetMemberIdentity(activeAgentMembers[0].memberIdentity);
    }
  }, [activeAgentMembers, targetMemberIdentity]);

  const disabledReason = channelError
    ? 'Channel unavailable. Check den-channels API health.'
    : null;
  const identityRequired = normalizedSenderIdentity.length === 0;
  const directModeRequiresTarget = sendMode === 'direct' && !selectedTarget;
  const isComposerDisabled = !activeChannel || sending || Boolean(disabledReason) || identityRequired || directModeRequiresTarget;
  const channelStatus = channelLoading && !activeChannel
    ? 'loading channels…'
    : channelError
      ? channelError.message
      : activeChannel
        ? `${activeChannel.displayName} · ${activeChannel.kind} · ${activeAgentMembers.length} active agent binding${activeAgentMembers.length === 1 ? '' : 's'}`
        : 'No project channel selected';

  const handleSenderIdentityChange = useCallback((event: ChangeEvent<HTMLInputElement>) => {
    const value = event.target.value;
    setSenderIdentity(value);
    persistSenderIdentity(value);
  }, []);

  const handleScrollbackScroll = useCallback((event: UIEvent<HTMLDivElement>) => {
    isScrollPinnedToBottomRef.current = isScrollElementPinnedToBottom(event.currentTarget);
  }, []);

  const insertMention = useCallback((identity: string) => {
    if (!mentionQuery) return;
    setDraft(current => insertMentionToken(current, mentionQuery, identity));
    setMentionActiveIndex(0);
  }, [mentionQuery]);

  const handleComposerKeyDown = useCallback((event: KeyboardEvent<HTMLInputElement>) => {
    if (!mentionQuery || mentionSuggestions.length === 0) return;
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setMentionActiveIndex(index => (index + 1) % mentionSuggestions.length);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setMentionActiveIndex(index => (index - 1 + mentionSuggestions.length) % mentionSuggestions.length);
    } else if (event.key === 'Enter' || event.key === 'Tab') {
      event.preventDefault();
      insertMention(mentionSuggestions[mentionActiveIndex]?.identity ?? mentionSuggestions[0].identity);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      setMentionActiveIndex(0);
      setDraft(current => current);
    }
  }, [insertMention, mentionActiveIndex, mentionQuery, mentionSuggestions]);

  const handleSubmit = useCallback(async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const body = draft.trim();
    if (!activeChannel || !body || isComposerDisabled || !normalizedSenderIdentity) return;

    setSending(true);
    setSendError(null);
    try {
      if (sendMode === 'direct' && selectedTarget) {
        const result = await postGatewayDirectAgentMessage({
          channelId: activeChannel.id,
          memberIdentity: selectedTarget.memberIdentity,
          senderIdentity: normalizedSenderIdentity,
          body,
        });
        setLastDirectResult(result);
      } else if (sendMode === 'channel') {
        await postChannelMessage(activeChannel.id, {
          senderType: 'user',
          senderIdentity: normalizedSenderIdentity,
          messageKind: 'human_text',
          body,
        });
        setLastDirectResult(null);
      }
      setDraft('');
      refreshMessages();
      refreshActivityEvents();
      refreshReactions();
    } catch (error) {
      setSendError(error instanceof Error ? error : new Error(String(error)));
    } finally {
      setSending(false);
    }
  }, [activeChannel, draft, isComposerDisabled, normalizedSenderIdentity, refreshActivityEvents, refreshMessages, refreshReactions, selectedTarget, sendMode]);

  const handleReactToMessage = useCallback(async (message: ChannelMessage, reactionKey: string) => {
    const reactorIdentity = normalizedSenderIdentity || targetMemberIdentity;
    if (!reactorIdentity) {
      setSendError(new Error('Set Posting as before reacting.'));
      return;
    }
    setSendError(null);
    try {
      await addChannelReaction(message.id, {
        reactorType: 'user',
        reactorIdentity,
        reactionKey,
      });
      refreshReactions();
    } catch (error) {
      setSendError(error instanceof Error ? error : new Error(String(error)));
    }
  }, [normalizedSenderIdentity, refreshReactions, targetMemberIdentity]);

  const handleInviteAgent = useCallback(async () => {
    const identity = inviteIdentity.trim();
    if (!activeChannel || !identity) return;
    setInviteSending(true);
    setSendError(null);
    try {
      await upsertChannelMembership(activeChannel.id, {
        memberType: 'agent',
        memberIdentity: identity,
        membershipStatus: inviteExistingMember?.membershipStatus ?? 'active',
        wakePolicy: inviteWakePolicy,
        canSend: inviteExistingMember?.canSend ?? true,
        canReact: inviteExistingMember?.canReact ?? true,
        canInvite: inviteExistingMember?.canInvite ?? false,
        cooldownSeconds: inviteExistingMember?.cooldownSeconds,
        maxAutoRepliesPerWindow: inviteExistingMember?.maxAutoRepliesPerWindow,
      });
      setInviteIdentity('');
      refreshMemberships();
    } catch (error) {
      setSendError(error instanceof Error ? error : new Error(String(error)));
    } finally {
      setInviteSending(false);
    }
  }, [activeChannel, inviteExistingMember, inviteIdentity, inviteWakePolicy, refreshMemberships]);

  const handleEditMember = useCallback((member: GatewayMember) => {
    setEditingMemberIdentity(member.memberIdentity);
    setEditingWakePolicy(member.wakePolicy || DEFAULT_WAKE_POLICY);
    setEditingMembershipStatus(member.membershipStatus || 'active');
  }, []);

  const handleSaveMemberSettings = useCallback(async () => {
    if (!activeChannel || !editingMember) return;
    setMemberSaving(true);
    setSendError(null);
    try {
      await upsertChannelMembership(activeChannel.id, {
        memberType: editingMember.memberType,
        memberIdentity: editingMember.memberIdentity,
        membershipStatus: editingMembershipStatus,
        wakePolicy: editingWakePolicy,
        canSend: editingMember.canSend,
        canReact: editingMember.canReact,
        canInvite: editingMember.canInvite,
        cooldownSeconds: editingMember.cooldownSeconds,
        maxAutoRepliesPerWindow: editingMember.maxAutoRepliesPerWindow,
      });
      setEditingMemberIdentity(null);
      refreshMemberships();
    } catch (error) {
      setSendError(error instanceof Error ? error : new Error(String(error)));
    } finally {
      setMemberSaving(false);
    }
  }, [activeChannel, editingMember, editingMembershipStatus, editingWakePolicy, refreshMemberships]);

  const handleTestWake = useCallback(async () => {
    if (!activeChannel || !selectedTarget || !normalizedSenderIdentity) return;
    setWakeSending(true);
    setSendError(null);
    try {
      const result = await postGatewayTestWake({
        channelId: activeChannel.id,
        memberIdentity: selectedTarget.memberIdentity,
        requestedBy: normalizedSenderIdentity,
        note: 'den-channels-ui-controlled-probe',
      });
      setLastWakeResult(result);
      refreshMessages();
    } catch (error) {
      setSendError(error instanceof Error ? error : new Error(String(error)));
    } finally {
      setWakeSending(false);
    }
  }, [activeChannel, normalizedSenderIdentity, refreshMessages, selectedTarget]);

  const composerPlaceholder = identityRequired
    ? 'Set Posting as before sending'
    : sendMode === 'direct' && selectedTarget
        ? `Direct message ${selectedTarget.memberIdentity} in ${channelLabel(activeChannel, projectId)}`
        : sendMode === 'direct'
          ? 'Join or select an agent before sending a direct message'
          : `Message ${channelLabel(activeChannel, projectId)}`;

  useEffect(() => {
    const autoScrollKey = `${scrollResetKey ?? projectId ?? 'aggregate'}:${activeChannel?.id ?? 'none'}`;
    if (previousAutoScrollKeyRef.current !== autoScrollKey) {
      previousAutoScrollKeyRef.current = autoScrollKey;
      pendingAutoScrollSnapKeyRef.current = autoScrollKey;
      isScrollPinnedToBottomRef.current = true;
    }

    if (!autoScroll) return;

    const shouldSnapToBottom = activeChannel !== null && pendingAutoScrollSnapKeyRef.current === autoScrollKey;
    if (!shouldSnapToBottom && !isScrollPinnedToBottomRef.current) return;

    scrollAnchorRef.current?.scrollIntoView({
      block: 'end',
      behavior: shouldSnapToBottom ? 'auto' : 'smooth',
    });
    isScrollPinnedToBottomRef.current = true;

    if (shouldSnapToBottom && !messagesLoading && !activityLoading) {
      pendingAutoScrollSnapKeyRef.current = null;
    }
  }, [activeChannel, activeChannel?.id, activityEvents?.length, activityLoading, autoScroll, messagesLoading, projectId, scrollResetKey, sortedMessages.length]);

  return (
    <section className={`panel channel-chat-panel channel-chat-panel-size-${panelSize}`} aria-label="Project channel chat">
      <div className="channel-chat-header">
        <div className="channel-chat-title">
          <span className="channel-chat-kicker">Channel</span>
          <strong>{channelLabel(activeChannel, projectId)}</strong>
          <span>{channelStatus}</span>
        </div>
        <div className="channel-chat-size-controls" role="group" aria-label="Channel panel size">
          {PANEL_SIZE_OPTIONS.map(option => (
            <button
              key={option.value}
              type="button"
              className={`channel-chat-size-button ${panelSize === option.value ? 'active' : ''}`}
              aria-pressed={panelSize === option.value}
              onClick={() => onPanelSizeChange(option.value)}
              title={`Set channel panel size to ${option.label.toLowerCase()}`}
            >
              {option.label}
            </button>
          ))}
        </div>
        <label className="channel-chat-auto-scroll">
          <input
            type="checkbox"
            checked={autoScroll}
            onChange={event => setAutoScroll(event.target.checked)}
          />
          <span>Auto-scroll</span>
        </label>
        <label className="channel-chat-identity-label" htmlFor="channel-chat-sender-identity">Posting as</label>
        <input
          id="channel-chat-sender-identity"
          className="channel-chat-identity"
          value={senderIdentity}
          onChange={handleSenderIdentityChange}
          placeholder="your name"
          spellCheck={false}
          autoComplete="nickname"
        />
        <label className="channel-chat-selector-label" htmlFor="channel-chat-selector">Channel</label>
        <select
          id="channel-chat-selector"
          className="channel-chat-selector"
          value={activeChannel?.id ?? ''}
          disabled={availableChannels.length === 0}
          onChange={event => setSelectedChannelId(Number(event.target.value))}
          title="Select a project/space channel."
        >
          {availableChannels.length === 0 ? (
            <option value="">{channelLabel(activeChannel, projectId)}</option>
          ) : availableChannels.map(candidate => (
            <option key={candidate.id} value={candidate.id}>{channelLabel(candidate, projectId)}</option>
          ))}
        </select>
        <button
          type="button"
          className="channel-chat-refresh"
          onClick={() => {
            refreshChannels();
            refreshMessages();
            refreshActivityEvents();
            refreshReactions();
            refreshMemberships();
          }}
        >
          Refresh
        </button>
      </div>

      <div className="channel-chat-body-region">
        <div className="channel-chat-scrollback" aria-live="polite" ref={scrollbackRef} onScroll={handleScrollbackScroll}>
          {disabledReason ? (
            <div className="channel-chat-state channel-chat-state-muted">{disabledReason}</div>
          ) : (messagesLoading || activityLoading) && sortedMessages.length === 0 && unanchoredActivityEvents.length === 0 && deliveryProgressBlocks.length === 0 ? (
            <div className="channel-chat-state">Loading channel messages…</div>
          ) : messagesError || activityError ? (
            <div className="channel-chat-state channel-chat-state-error">{(messagesError ?? activityError)?.message}</div>
          ) : sortedMessages.length === 0 && unanchoredActivityEvents.length === 0 && deliveryProgressBlocks.length === 0 ? (
            <div className="channel-chat-state channel-chat-state-muted">No channel messages yet. Start the scrollback below.</div>
          ) : (
            <>
              <ActivityTimeline events={unanchoredActivityEvents} />
              <DeliveryProgressCards blocks={deliveryProgressBlocks} />
              {sortedMessages.map(message => {
                const evidence = directMessageEvidence(message);
                const wakeProgress = deriveWakeProgress(message, sortedMessages);
                const messageReactions = reactionsByMessageId.get(message.id) ?? [];
                const anchoredActivityEvents = activityEventsByMessageId.get(message.id) ?? [];
                return (
                <div key={message.id} className="channel-chat-message">
                  <span className="message-time">{formatTimeAgo(message.createdAt)}</span>
                  <span className={`channel-chat-sender channel-chat-sender-${message.senderType}`}>{messageSender(message)}</span>
                  <span className="channel-chat-body">
                    <MessageBody body={message.body} />
                    <ActivityTimeline events={anchoredActivityEvents} compact />
                    {wakeProgress && (
                      <span className={`channel-chat-wake-progress channel-chat-wake-progress-${wakeProgress.state}`}>
                        <strong>{wakeProgress.label}</strong>
                        <span>{wakeProgress.detail}</span>
                      </span>
                    )}
                    {evidence && (
                      <span className="channel-chat-delivery-status">
                        <strong>{evidence.target ? `Direct to ${evidence.target}` : 'Direct agent request'}</strong>
                        <span>{evidence.status}</span>
                        <a href={evidence.url} target="_blank" rel="noreferrer">Gateway evidence</a>
                      </span>
                    )}
                    <span className="channel-chat-reactions" aria-label={`Reactions for message ${message.id}`}>
                      {messageReactions.map(reaction => (
                        <span key={`${reaction.channelMessageId}:${reaction.reactionKey}`} className="channel-chat-reaction-pill" title={reaction.reactors.join(', ')}>
                          <span>{reaction.reactionKey}</span>
                          <span>{reaction.count}</span>
                        </span>
                      ))}
                      <span className="channel-chat-reaction-actions" aria-label="Quick reactions">
                        {QUICK_REACTIONS.map(reactionKey => (
                          <button
                            key={reactionKey}
                            type="button"
                            onClick={() => handleReactToMessage(message, reactionKey)}
                            disabled={identityRequired}
                            title={`React ${reactionKey} without creating a wake pulse`}
                          >
                            {reactionKey}
                          </button>
                        ))}
                      </span>
                    </span>
                  </span>
                </div>
              );
              })}
            </>
          )}
          <div className="channel-chat-scroll-anchor" ref={scrollAnchorRef} aria-hidden="true" />
        </div>

        <aside className="channel-chat-members" aria-label="Channel participants and active Hermes profile bindings">
          <section className="channel-chat-members-panel" aria-label="Channel participants">
            <div className="channel-chat-members-header">
              <strong>Participants</strong>
              <span>{membershipsLoading ? 'loading…' : `${members.length} total`}</span>
            </div>
            <div className="channel-chat-members-list">
              {membershipsError ? (
                <div className="channel-chat-state channel-chat-state-error">{membershipsError.message}</div>
              ) : members.length === 0 ? (
                <div className="channel-chat-state channel-chat-state-muted">No joined agents yet.</div>
              ) : members.map(member => {
                const activity = memberActivityByIdentity.get(member.memberIdentity) ?? 'active';
                const activityClass = activity === 'working' ? 'channel-chat-member-working' : 'channel-chat-member-active';
                const status = memberStatus(member);
                const visibleStatus = activity === 'working' ? status.replace(/^active/, 'working') : status;
                return (
                  <div
                    key={member.id}
                    className={`channel-chat-member-row ${member.memberIdentity === targetMemberIdentity ? 'selected' : ''}`}
                  >
                    <button
                      type="button"
                      className={`channel-chat-member ${activityClass}`}
                      onClick={() => memberIsActiveAgent(member) && setTargetMemberIdentity(member.memberIdentity)}
                      disabled={!memberIsActiveAgent(member)}
                      title={visibleStatus}
                    >
                      <span className={`channel-chat-member-type member-type-${member.memberType}`}>{member.memberType}</span>
                      <span className="channel-chat-member-identity">{member.memberIdentity}</span>
                      <span className={`member-activity member-activity-${activity}`}>{activity}</span>
                      <span className="channel-chat-member-status">{visibleStatus}</span>
                    </button>
                    {member.memberType === 'agent' && (
                      <button
                        type="button"
                        className="channel-chat-member-edit"
                        onClick={() => handleEditMember(member)}
                        disabled={!activeChannel || memberSaving}
                        aria-label={`Edit wake policy for ${member.memberIdentity}`}
                      >
                        Edit
                      </button>
                    )}
                  </div>
                );
              })}
            </div>
            {editingMember && (
              <div className="channel-chat-member-editor" aria-label={`Edit ${editingMember.memberIdentity} membership settings`}>
                <div className="channel-chat-member-editor-title">
                  <strong>Editing {editingMember.memberIdentity}</strong>
                  <span>Changes affect future wake routing only.</span>
                </div>
                <label>
                  <span>Wake policy</span>
                  <select
                    value={editingWakePolicy}
                    onChange={event => setEditingWakePolicy(event.target.value)}
                    disabled={memberSaving}
                  >
                    {WAKE_POLICY_OPTIONS.map(option => (
                      <option key={option.value} value={option.value}>{option.label}</option>
                    ))}
                  </select>
                </label>
                <label>
                  <span>Status</span>
                  <select
                    value={editingMembershipStatus}
                    onChange={event => setEditingMembershipStatus(event.target.value)}
                    disabled={memberSaving}
                  >
                    {MEMBERSHIP_STATUS_OPTIONS.map(option => (
                      <option key={option.value} value={option.value}>{option.label}</option>
                    ))}
                  </select>
                </label>
                <div className="channel-chat-member-editor-actions">
                  <button type="button" onClick={handleSaveMemberSettings} disabled={memberSaving}>
                    {memberSaving ? 'Saving…' : 'Save settings'}
                  </button>
                  <button type="button" onClick={() => setEditingMemberIdentity(null)} disabled={memberSaving}>
                    Cancel
                  </button>
                </div>
              </div>
            )}
          </section>
          <section className="channel-chat-debug-panel" aria-label="Wake debug controls and evidence">
            <div className="channel-chat-debug-header">
              <strong>Wake debug</strong>
              <span>Manual test tools</span>
            </div>
            <div className="channel-chat-invite">
              <input
                value={inviteIdentity}
                onChange={event => setInviteIdentity(event.target.value)}
                placeholder="agent identity"
                disabled={!activeChannel || inviteSending}
                aria-label="Agent identity to join"
              />
              <select
                value={inviteWakePolicy}
                onChange={event => setInviteWakePolicy(event.target.value)}
                disabled={!activeChannel || inviteSending}
                aria-label="Wake policy"
              >
                {WAKE_POLICY_OPTIONS.map(option => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
              <button type="button" onClick={handleInviteAgent} disabled={!activeChannel || inviteSending || inviteIdentity.trim().length === 0}>
                {inviteSending ? (inviteExistingMember ? 'Updating…' : 'Joining…') : (inviteExistingMember ? 'Update agent' : 'Join agent')}
              </button>
              <span className="channel-chat-routing-note">Wake policy changes apply to future deliveries only.</span>
            </div>
            <button
              type="button"
              className="channel-chat-test-wake"
              onClick={handleTestWake}
              disabled={!activeChannel || !selectedTarget || wakeSending || identityRequired}
            >
              {wakeSending ? 'Recording wake…' : 'Test wake selected'}
            </button>
            {lastWakeResult && (
              <div className="channel-chat-wake-result">
                <strong>{lastWakeResult.status}</strong>
                <span>{lastWakeResult.memberIdentity} · message {lastWakeResult.messageId}</span>
                <span>{lastWakeResult.evidenceSummary}</span>
              </div>
            )}
            {lastDirectResult && (
              <div className="channel-chat-wake-result">
                <strong>{lastDirectResult.deliveryStatus}</strong>
                <span>
                  {lastDirectResult.memberIdentity} · request {lastDirectResult.requestId} · claim {lastDirectResult.claimStatus} · completion {lastDirectResult.completionStatus} · suppression {lastDirectResult.suppressionStatus}
                </span>
                <a href={lastDirectResult.gatewayMessageUrl} target="_blank" rel="noreferrer">Gateway message evidence</a>
                <a href={lastDirectResult.gatewayEventsUrl} target="_blank" rel="noreferrer">Gateway events evidence</a>
                <span>{lastDirectResult.evidenceSummary}</span>
              </div>
            )}
          </section>
        </aside>
      </div>

      {(channelError || messagesError || activityError || membershipsError || sendError) && (
        <div className="channel-chat-error">
          {(sendError ?? membershipsError ?? activityError ?? messagesError ?? channelError)?.message}
        </div>
      )}

      <form className="channel-chat-composer" onSubmit={handleSubmit}>
        <select
          className="channel-chat-send-mode"
          value={sendMode}
          onChange={event => setSendMode(event.target.value as ChannelSendMode)}
          disabled={!activeChannel || sending || Boolean(disabledReason) || identityRequired}
          aria-label="Send mode"
          title="Choose whether to post to the whole channel or directly wake one agent."
        >
          <option value="channel">Channel</option>
          <option value="direct">Direct agent</option>
        </select>
        <select
          value={targetMemberIdentity}
          onChange={event => setTargetMemberIdentity(event.target.value)}
          disabled={sendMode === 'channel' || activeAgentMembers.length === 0 || !activeChannel || sending || Boolean(disabledReason) || identityRequired}
          aria-label="Direct agent target"
        >
          {activeAgentMembers.length === 0 ? (
            <option value="">No active agents</option>
          ) : activeAgentMembers.map(member => (
            <option key={member.id} value={member.memberIdentity}>@{member.memberIdentity}</option>
          ))}
        </select>
        <input
          value={draft}
          onChange={event => setDraft(event.target.value)}
          onKeyDown={handleComposerKeyDown}
          placeholder={composerPlaceholder}
          disabled={isComposerDisabled}
          aria-label="Channel message"
        />
        {mentionQuery && (
          <div className="channel-chat-mention-menu" role="listbox" aria-label="Mention suggestions">
            {mentionSuggestions.length === 0 ? (
              <div className="channel-chat-mention-empty">No active channel members match @{mentionQuery.query}</div>
            ) : mentionSuggestions.map((suggestion, index) => (
              <button
                key={suggestion.identity}
                type="button"
                className={`channel-chat-mention-option ${index === mentionActiveIndex ? 'active' : ''}`}
                onMouseDown={event => event.preventDefault()}
                onClick={() => insertMention(suggestion.identity)}
                role="option"
                aria-selected={index === mentionActiveIndex}
              >
                <span>{suggestion.label}</span>
              </button>
            ))}
          </div>
        )}
        <button type="submit" disabled={isComposerDisabled || draft.trim().length === 0}>
          {sending ? 'Sending…' : 'Send'}
        </button>
      </form>
    </section>
  );
}
