import { useCallback, useMemo, useState } from 'react';
import type { FormEvent } from 'react';
import type { Channel, ChannelMessage } from '../api/types';
import {
  ensureProjectDefaultChannel,
  listChannelMessages,
  listChannels,
  postChannelMessage,
} from '../api/client';
import { usePolling } from '../hooks/usePolling';
import { formatTimeAgo } from '../utils';

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

export function ChannelChatPanel({ projectId, spaceName }: Props) {
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<Error | null>(null);

  const fetchChannel = useCallback(async () => {
    if (!projectId) return null;
    const [existing] = await listChannels({ projectId, kind: 'project_default', limit: 1 });
    if (existing) return existing;
    return ensureProjectDefaultChannel(projectId, {
      displayName: spaceName?.trim() || projectId,
      createdBy: 'den-web',
    });
  }, [projectId, spaceName]);

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
  const isComposerDisabled = !activeChannel || sending || Boolean(disabledReason);
  const channelStatus = channelLoading && !activeChannel
    ? 'loading channel…'
    : channelError
      ? channelError.message
      : activeChannel
        ? `${activeChannel.displayName} · ${activeChannel.kind}`
        : 'No project channel selected';

  const handleSubmit = useCallback(async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const body = draft.trim();
    if (!activeChannel || !body || isComposerDisabled) return;

    setSending(true);
    setSendError(null);
    try {
      await postChannelMessage(activeChannel.id, {
        senderType: 'user',
        senderIdentity: 'web-ui',
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
  }, [activeChannel, draft, isComposerDisabled, refreshMessages]);

  return (
    <section className="panel channel-chat-panel" aria-label="Project channel chat">
      <div className="channel-chat-header">
        <div className="channel-chat-title">
          <span className="channel-chat-kicker">Channel</span>
          <strong>{channelLabel(activeChannel, projectId)}</strong>
          <span>{channelStatus}</span>
        </div>
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
          placeholder={projectId ? `Message ${channelLabel(activeChannel, projectId)}` : 'Select a project to chat'}
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
