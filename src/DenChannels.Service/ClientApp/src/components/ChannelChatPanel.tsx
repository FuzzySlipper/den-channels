import { useCallback, useMemo, useState } from 'react';
import type { ChangeEvent, FormEvent } from 'react';
import type { Channel, ChannelMessage } from '../api/types';
import {
  ensureProjectDefaultChannel,
  listChannelMessages,
  listChannels,
  postChannelMessage,
} from '../api/client';
import { usePolling } from '../hooks/usePolling';
import { formatTimeAgo } from '../utils';

const SENDER_IDENTITY_STORAGE_KEY = 'den-channel-sender-identity';

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

export function ChannelChatPanel({ projectId, spaceName }: Props) {
  const [draft, setDraft] = useState('');
  const [senderIdentity, setSenderIdentity] = useState(readStoredSenderIdentity);
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<Error | null>(null);
  const normalizedSenderIdentity = senderIdentity.trim();

  const fetchChannel = useCallback(async () => {
    if (!projectId) return null;
    const [existing] = await listChannels({ projectId, kind: 'project_default', limit: 1 });
    if (existing) return existing;
    return ensureProjectDefaultChannel(projectId, {
      displayName: spaceName?.trim() || projectId,
      createdBy: normalizedSenderIdentity || 'den-web',
    });
  }, [normalizedSenderIdentity, projectId, spaceName]);

  const {
    data: channel,
    loading: channelLoading,
    error: channelError,
    refresh: refreshChannel,
  } = usePolling<Channel | null>(fetchChannel, 15000);

  const activeChannel = channel?.projectId === projectId ? channel : null;

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

  const sortedMessages = useMemo(() => {
    const visibleMessages = activeChannel
      ? (messages ?? []).filter(message => message.channelId === activeChannel.id)
      : [];
    return [...visibleMessages].sort((left, right) => left.id - right.id);
  }, [activeChannel, messages]);

  const disabledReason = !projectId
    ? 'Select a project space to join its default channel.'
    : channelError
      ? 'Channel unavailable. Check den-channels API health.'
      : null;
  const identityRequired = Boolean(projectId) && normalizedSenderIdentity.length === 0;
  const isComposerDisabled = !activeChannel || sending || Boolean(disabledReason) || identityRequired;
  const channelStatus = channelLoading && !activeChannel
    ? 'loading channel…'
    : channelError
      ? channelError.message
      : activeChannel
        ? `${activeChannel.displayName} · ${activeChannel.kind}`
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
      await postChannelMessage(activeChannel.id, {
        senderType: 'user',
        senderIdentity: normalizedSenderIdentity,
        messageKind: 'human_text',
        body,
      });
      setDraft('');
      refreshMessages();
    } catch (error) {
      setSendError(error instanceof Error ? error : new Error(String(error)));
    } finally {
      setSending(false);
    }
  }, [activeChannel, draft, isComposerDisabled, normalizedSenderIdentity, refreshMessages]);

  const composerPlaceholder = !projectId
    ? 'Select a project to chat'
    : identityRequired
      ? 'Set Posting as before sending'
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
          disabled
          title="Channel selector placeholder; project default channel is selected automatically."
        >
          <option value={activeChannel?.id ?? ''}>{channelLabel(activeChannel, projectId)}</option>
        </select>
        <button
          type="button"
          className="channel-chat-refresh"
          onClick={() => {
            refreshChannel();
            refreshMessages();
          }}
        >
          Refresh
        </button>
      </div>

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
          sortedMessages.map(message => (
            <div key={message.id} className="channel-chat-message">
              <span className="message-time">{formatTimeAgo(message.createdAt)}</span>
              <span className={`channel-chat-sender channel-chat-sender-${message.senderType}`}>{messageSender(message)}</span>
              <span className="channel-chat-body">{message.body}</span>
            </div>
          ))
        )}
      </div>

      {(channelError || messagesError || sendError) && (
        <div className="channel-chat-error">
          {(sendError ?? messagesError ?? channelError)?.message}
        </div>
      )}

      <form className="channel-chat-composer" onSubmit={handleSubmit}>
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
