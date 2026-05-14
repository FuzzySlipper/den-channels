import { useCallback, useEffect, useMemo, useState } from 'react';
import type { ChangeEvent, FormEvent } from 'react';
import type { Channel, ChannelMessage, GatewayDirectAgentMessage, GatewayMember, GatewayMemberships, GatewayTestWake } from '../api/types';
import {
  ensureProjectDefaultChannel,
  listChannelMessages,
  listChannels,
  listGatewayMemberships,
  postChannelMessage,
  postGatewayDirectAgentMessage,
  postGatewayTestWake,
  upsertChannelMembership,
} from '../api/client';
import { usePolling } from '../hooks/usePolling';
import { formatTimeAgo } from '../utils';

const SENDER_IDENTITY_STORAGE_KEY = 'den-channel-sender-identity';
const DEFAULT_WAKE_POLICY = 'mentions_only';

interface Props {
  projectId: string | null;
  spaceName?: string | null;
}

function channelLabel(channel: Channel | null, projectId: string | null): string {
  if (channel) return `#${channel.slug}`;
  if (projectId) return `#project-${projectId}`;
  return '#select-project';
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

function memberIsActiveAgent(member: GatewayMember): boolean {
  return member.memberType === 'agent' && member.membershipStatus === 'active';
}

export function ChannelChatPanel({ projectId, spaceName }: Props) {
  const [draft, setDraft] = useState('');
  const [senderIdentity, setSenderIdentity] = useState(readStoredSenderIdentity);
  const [selectedChannelId, setSelectedChannelId] = useState<number | null>(null);
  const [targetMemberIdentity, setTargetMemberIdentity] = useState('');
  const [inviteIdentity, setInviteIdentity] = useState('');
  const [inviteWakePolicy, setInviteWakePolicy] = useState(DEFAULT_WAKE_POLICY);
  const [sending, setSending] = useState(false);
  const [inviteSending, setInviteSending] = useState(false);
  const [wakeSending, setWakeSending] = useState(false);
  const [sendError, setSendError] = useState<Error | null>(null);
  const [lastWakeResult, setLastWakeResult] = useState<GatewayTestWake | null>(null);
  const [lastDirectResult, setLastDirectResult] = useState<GatewayDirectAgentMessage | null>(null);
  const normalizedSenderIdentity = senderIdentity.trim();

  const fetchChannels = useCallback(async () => {
    if (!projectId) return [];
    const channels = await listChannels({ projectId, limit: 100 });
    if (channels.length > 0) return channels;
    const ensured = await ensureProjectDefaultChannel(projectId, {
      displayName: spaceName?.trim() || projectId,
      createdBy: normalizedSenderIdentity || 'den-web',
    });
    return [ensured];
  }, [normalizedSenderIdentity, projectId, spaceName]);

  const {
    data: channels,
    loading: channelLoading,
    error: channelError,
    refresh: refreshChannels,
  } = usePolling<Channel[]>(fetchChannels, 15000);

  const availableChannels = useMemo(
    () => (channels ?? []).filter(candidate => candidate.projectId === projectId),
    [channels, projectId],
  );

  useEffect(() => {
    if (availableChannels.length === 0) {
      setSelectedChannelId(null);
      return;
    }
    if (!selectedChannelId || !availableChannels.some(candidate => candidate.id === selectedChannelId)) {
      const defaultChannel = availableChannels.find(candidate => candidate.kind === 'project_default') ?? availableChannels[0];
      setSelectedChannelId(defaultChannel.id);
    }
  }, [availableChannels, selectedChannelId]);

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

  const members = memberships?.members ?? [];
  const activeAgentMembers = members.filter(memberIsActiveAgent);
  const selectedTarget = activeAgentMembers.find(member => member.memberIdentity === targetMemberIdentity) ?? null;

  useEffect(() => {
    if (activeAgentMembers.length === 0) {
      setTargetMemberIdentity('');
      return;
    }
    if (!targetMemberIdentity || !activeAgentMembers.some(member => member.memberIdentity === targetMemberIdentity)) {
      setTargetMemberIdentity(activeAgentMembers[0].memberIdentity);
    }
  }, [activeAgentMembers, targetMemberIdentity]);

  const disabledReason = !projectId
    ? 'Select a project space to join its default channel.'
    : channelError
      ? 'Channel unavailable. Check den-channels API health.'
      : null;
  const identityRequired = Boolean(projectId) && normalizedSenderIdentity.length === 0;
  const isComposerDisabled = !activeChannel || sending || Boolean(disabledReason) || identityRequired;
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

  const handleSubmit = useCallback(async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const body = draft.trim();
    if (!activeChannel || !body || isComposerDisabled || !normalizedSenderIdentity) return;

    setSending(true);
    setSendError(null);
    try {
      if (selectedTarget) {
        const result = await postGatewayDirectAgentMessage({
          channelId: activeChannel.id,
          memberIdentity: selectedTarget.memberIdentity,
          senderIdentity: normalizedSenderIdentity,
          body,
        });
        setLastDirectResult(result);
      } else {
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
    } catch (error) {
      setSendError(error instanceof Error ? error : new Error(String(error)));
    } finally {
      setSending(false);
    }
  }, [activeChannel, draft, isComposerDisabled, normalizedSenderIdentity, refreshMessages, selectedTarget]);

  const handleInviteAgent = useCallback(async () => {
    const identity = inviteIdentity.trim();
    if (!activeChannel || !identity) return;
    setInviteSending(true);
    setSendError(null);
    try {
      await upsertChannelMembership(activeChannel.id, {
        memberType: 'agent',
        memberIdentity: identity,
        membershipStatus: 'active',
        wakePolicy: inviteWakePolicy,
        canSend: true,
        canReact: true,
        canInvite: false,
      });
      setInviteIdentity('');
      refreshMemberships();
    } catch (error) {
      setSendError(error instanceof Error ? error : new Error(String(error)));
    } finally {
      setInviteSending(false);
    }
  }, [activeChannel, inviteIdentity, inviteWakePolicy, refreshMemberships]);

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

  const composerPlaceholder = !projectId
    ? 'Select a project to chat'
    : identityRequired
      ? 'Set Posting as before sending'
      : selectedTarget
        ? `Direct message ${selectedTarget.memberIdentity} in ${channelLabel(activeChannel, projectId)}`
        : `Message ${channelLabel(activeChannel, projectId)}`;

  return (
    <section className="panel channel-chat-panel" aria-label="Project channel chat">
      <div className="channel-chat-header">
        <div className="channel-chat-title">
          <span className="channel-chat-kicker">Channel</span>
          <strong>{channelLabel(activeChannel, projectId)}</strong>
          <span>{channelStatus}</span>
        </div>
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
            refreshMemberships();
          }}
        >
          Refresh
        </button>
      </div>

      <div className="channel-chat-body-region">
        <div className="channel-chat-scrollback" aria-live="polite">
          {disabledReason ? (
            <div className="channel-chat-state channel-chat-state-muted">{disabledReason}</div>
          ) : messagesLoading && sortedMessages.length === 0 ? (
            <div className="channel-chat-state">Loading channel messages…</div>
          ) : messagesError ? (
            <div className="channel-chat-state channel-chat-state-error">{messagesError.message}</div>
          ) : sortedMessages.length === 0 ? (
            <div className="channel-chat-state channel-chat-state-muted">No channel messages yet. Start the scrollback below.</div>
          ) : (
            sortedMessages.map(message => {
              const evidence = directMessageEvidence(message);
              return (
                <div key={message.id} className="channel-chat-message">
                  <span className="message-time">{formatTimeAgo(message.createdAt)}</span>
                  <span className={`channel-chat-sender channel-chat-sender-${message.senderType}`}>{messageSender(message)}</span>
                  <span className="channel-chat-body">
                    {message.body}
                    {evidence && (
                      <span className="channel-chat-delivery-status">
                        <strong>{evidence.target ? `Direct to ${evidence.target}` : 'Direct agent request'}</strong>
                        <span>{evidence.status}</span>
                        <a href={evidence.url} target="_blank" rel="noreferrer">Gateway evidence</a>
                      </span>
                    )}
                  </span>
                </div>
              );
            })
          )}
        </div>

        <aside className="channel-chat-members" aria-label="Channel participants and active Hermes profile bindings">
          <div className="channel-chat-members-header">
            <strong>Participants</strong>
            <span>{membershipsLoading ? 'loading…' : `${members.length} total`}</span>
          </div>
          <div className="channel-chat-members-list">
            {membershipsError ? (
              <div className="channel-chat-state channel-chat-state-error">{membershipsError.message}</div>
            ) : members.length === 0 ? (
              <div className="channel-chat-state channel-chat-state-muted">No joined agents yet.</div>
            ) : members.map(member => (
              <button
                key={member.id}
                type="button"
                className={`channel-chat-member ${member.memberIdentity === targetMemberIdentity ? 'selected' : ''}`}
                onClick={() => memberIsActiveAgent(member) && setTargetMemberIdentity(member.memberIdentity)}
                disabled={!memberIsActiveAgent(member)}
                title={memberStatus(member)}
              >
                <span className={`channel-chat-member-type member-type-${member.memberType}`}>{member.memberType}</span>
                <span className="channel-chat-member-identity">{member.memberIdentity}</span>
                <span className="channel-chat-member-status">{memberStatus(member)}</span>
              </button>
            ))}
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
              <option value="never">never</option>
              <option value="mentions_only">mentions only</option>
              <option value="direct_questions_only">direct questions</option>
              <option value="substantive_digest">substantive digest</option>
              <option value="all_human_messages">all human</option>
              <option value="all_messages_except_self">all except self</option>
            </select>
            <button type="button" onClick={handleInviteAgent} disabled={!activeChannel || inviteSending || inviteIdentity.trim().length === 0}>
              {inviteSending ? 'Joining…' : 'Join agent'}
            </button>
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
        </aside>
      </div>

      {(channelError || messagesError || membershipsError || sendError) && (
        <div className="channel-chat-error">
          {(sendError ?? membershipsError ?? messagesError ?? channelError)?.message}
        </div>
      )}

      <form className="channel-chat-composer" onSubmit={handleSubmit}>
        <select
          value={targetMemberIdentity}
          onChange={event => setTargetMemberIdentity(event.target.value)}
          disabled={activeAgentMembers.length === 0 || isComposerDisabled}
          aria-label="Direct agent target"
        >
          {activeAgentMembers.length === 0 ? (
            <option value="">channel message</option>
          ) : activeAgentMembers.map(member => (
            <option key={member.id} value={member.memberIdentity}>@{member.memberIdentity}</option>
          ))}
        </select>
        <input
          value={draft}
          onChange={event => setDraft(event.target.value)}
          placeholder={composerPlaceholder}
          disabled={isComposerDisabled}
          aria-label="Channel message"
        />
        <button type="submit" disabled={isComposerDisabled || draft.trim().length === 0}>
          {sending ? 'Sending…' : 'Send'}
        </button>
      </form>
    </section>
  );
}
