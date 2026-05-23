import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ChangeEvent, FormEvent } from 'react';
import type { Channel, ChannelMessage, DesktopSessionEvent, DesktopSessionSnapshot, GatewayMember, GatewayMemberships } from '../api/types';
import {
  ensureProjectDefaultChannel,
  listChannelMessages,
  listChannels,
  listDesktopSessionEvents,
  listDesktopSessionSnapshots,
  listGatewayMemberships,
  postChannelMessage,
} from '../api/client';
import { usePolling } from '../hooks/usePolling';
import { formatTimeAgo, truncate } from '../utils';

const SENDER_IDENTITY_STORAGE_KEY = 'den-channel-sender-identity';
const RECENT_STATUS_VALUES = new Set(['exited', 'exit', 'complete', 'completed', 'done', 'failed', 'crashed', 'terminated']);
const NOISY_EVENT_TYPES = new Set(['snapshot_published', 'snapshot_publish_failed', 'discovered']);

interface Props {
  projectId: string | null;
  spaceName?: string | null;
}

type FocusedLaneKind = 'session' | 'channel';
type FocusedLaneState = 'active' | 'working' | 'queued' | 'replied' | 'failed' | 'stale';

interface FocusedSessionLane {
  key: string;
  kind: FocusedLaneKind;
  channel: Channel;
  snapshot: DesktopSessionSnapshot | null;
  label: string;
  state: FocusedLaneState;
  lastActivityAt: string;
  threadId: number | null;
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
    // Embedded/private windows can reject storage writes; keep the in-memory identity.
  }
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null;
}

function stringFromUnknown(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;
}

function firstString(records: Array<Record<string, unknown> | null>, keys: string[]): string | null {
  for (const record of records) {
    if (!record) continue;
    for (const key of keys) {
      const value = stringFromUnknown(record[key]);
      if (value) return value;
    }
  }
  return null;
}

function parseJsonRecord(json: string | null): Record<string, unknown> | null {
  if (!json) return null;
  try {
    return asRecord(JSON.parse(json));
  } catch {
    return null;
  }
}

function channelLabel(channel: Channel | null, projectId: string | null): string {
  if (channel) return `#${channel.slug}`;
  return projectId ? `#project-${projectId}` : '#select-project';
}

function displayTime(value: string | null | undefined): string {
  return value ? formatTimeAgo(value) : 'no activity';
}

function messageSender(message: ChannelMessage): string {
  return message.senderIdentity || message.senderType;
}


function sessionActivityAt(snapshot: DesktopSessionSnapshot): string {
  return snapshot.last_activity_at ?? snapshot.updated_at ?? snapshot.received_at ?? snapshot.observed_at ?? snapshot.started_at ?? '';
}

function normalizedSessionStatus(snapshot: DesktopSessionSnapshot | null): string {
  return snapshot?.status?.trim().toLowerCase() ?? '';
}

function isLiveSession(snapshot: DesktopSessionSnapshot): boolean {
  const status = normalizedSessionStatus(snapshot);
  return !snapshot.is_stale && !snapshot.exited_at && !RECENT_STATUS_VALUES.has(status);
}

function deriveSessionState(snapshot: DesktopSessionSnapshot | null, members: GatewayMember[]): FocusedLaneState {
  if (!snapshot) {
    return members.some(member => member.memberType === 'agent' && member.membershipStatus === 'active') ? 'active' : 'queued';
  }

  const status = normalizedSessionStatus(snapshot);
  if (snapshot.is_stale) return 'stale';
  if (status.includes('fail') || status.includes('crash') || (snapshot.exit_code ?? 0) !== 0) return 'failed';
  if (status.includes('reply') || status.includes('complete') || status.includes('done')) return 'replied';
  if (snapshot.current_command || snapshot.current_phase || status.includes('run') || status.includes('work')) return 'working';
  if (status.includes('queue') || status.includes('pending') || status.includes('created')) return 'queued';
  return 'active';
}

function sessionModelProfile(snapshot: DesktopSessionSnapshot | null): string | null {
  if (!snapshot) return null;
  const capability = asRecord(snapshot.capabilities);
  const recent = asRecord(snapshot.recent_activity);
  const control = asRecord(snapshot.control_capabilities);
  const model = firstString([recent, capability, control], ['model', 'modelName', 'requested_model']);
  const profile = firstString([recent, capability, control], ['profile', 'profileName', 'provider', 'backend']);
  return [model, profile].filter(Boolean).join(' / ') || snapshot.backend || null;
}

function sessionDisplayName(snapshot: DesktopSessionSnapshot): string {
  return snapshot.display_name
    ?? snapshot.title
    ?? snapshot.agent_identity
    ?? snapshot.session_id;
}

function laneKeyForSession(snapshot: DesktopSessionSnapshot): string {
  return `session:${snapshot.source_instance_id}:${snapshot.session_id}`;
}

function laneKeyForChannel(channel: Channel): string {
  return `channel:${channel.id}`;
}

function buildLaneLabel(
  channel: Channel,
  snapshot: DesktopSessionSnapshot | null,
  state: FocusedLaneState,
  threadId: number | null,
  projectId: string | null,
): string {
  const project = snapshot?.project_id ?? channel.projectId ?? projectId ?? 'project';
  const task = snapshot?.task_id != null ? `task #${snapshot.task_id}` : 'no task';
  const thread = threadId != null ? `thread #${threadId}` : 'no thread';
  const agent = snapshot?.agent_identity ?? 'channel lane';
  const status = snapshot?.status ?? state;
  const lastActivity = displayTime(snapshot ? sessionActivityAt(snapshot) : channel.updatedAt);
  const modelProfile = sessionModelProfile(snapshot);
  const sessionName = snapshot ? sessionDisplayName(snapshot) : channel.displayName;
  return [
    `${state}: ${sessionName}`,
    `Project ${project}`,
    `Channel ${channelLabel(channel, projectId)}`,
    task,
    thread,
    agent,
    status,
    `last ${lastActivity}`,
    modelProfile ? `model/profile ${modelProfile}` : null,
  ].filter(Boolean).join(' · ');
}

function directMessageEvidence(message: ChannelMessage): { target: string | null; status: string } | null {
  const metadata = parseJsonRecord(message.metadataJson);
  if (!metadata) return null;
  if (metadata.deliveryMode !== 'direct_agent_message') return null;
  const target = stringFromUnknown(metadata.targetMemberIdentity);
  const status = [
    stringFromUnknown(metadata.deliveryStatus),
    stringFromUnknown(metadata.claimStatus),
    stringFromUnknown(metadata.completionStatus),
    stringFromUnknown(metadata.suppressionStatus),
  ].filter(Boolean).join(' · ');
  return { target, status: status || 'claimed/delivered/reply-posted pending' };
}

function isCommandOrResultMessage(message: ChannelMessage): boolean {
  const sourceKind = (message.sourceKind ?? '').toLowerCase();
  const messageKind = message.messageKind.toLowerCase();
  const body = message.body.trim();
  return body.startsWith('/')
    || sourceKind.includes('command')
    || sourceKind.includes('result')
    || sourceKind.includes('gateway')
    || sourceKind.includes('wake')
    || sourceKind.includes('external_adapter')
    || messageKind.includes('command')
    || messageKind.includes('result');
}

function eventPayloadPreview(event: DesktopSessionEvent): string {
  const payload = event.payload?.trim();
  if (!payload) return event.reason ?? 'status update';
  try {
    const parsed = JSON.parse(payload) as unknown;
    if (typeof parsed === 'string') return truncate(parsed, 180);
    const record = asRecord(parsed);
    if (!record) return truncate(JSON.stringify(parsed), 180);
    const concise: Record<string, unknown> = {};
    for (const key of ['status', 'phase', 'tool', 'tool_name', 'toolName', 'command', 'result', 'summary', 'message', 'error']) {
      if (record[key] != null) concise[key] = record[key];
    }
    return truncate(JSON.stringify(Object.keys(concise).length > 0 ? concise : parsed), 180);
  } catch {
    return truncate(payload, 180);
  }
}

function currentToolLabel(snapshot: DesktopSessionSnapshot | null, events: DesktopSessionEvent[]): string {
  if (snapshot?.current_command) return snapshot.current_command;
  for (const event of events) {
    const record = parseJsonRecord(event.payload);
    const tool = firstString([record], ['tool', 'tool_name', 'toolName', 'current_tool']);
    if (tool) return tool;
  }
  return 'No current tool signal';
}

function statusDetail(snapshot: DesktopSessionSnapshot | null): string {
  if (!snapshot) return 'Channel lane selected; waiting for durable session snapshot evidence.';
  const parts = [
    snapshot.status ?? 'unknown status',
    snapshot.current_phase ? `phase ${snapshot.current_phase}` : null,
    snapshot.current_command ? `command ${snapshot.current_command}` : null,
    snapshot.is_stale ? 'stale binding warning' : null,
    snapshot.exit_code != null ? `exit ${snapshot.exit_code}` : null,
  ];
  return parts.filter(Boolean).join(' · ');
}

function safeEvidenceLink(link: string | null): string | null {
  const trimmed = link?.trim();
  if (!trimmed) return null;
  if (trimmed.startsWith('/') && !trimmed.startsWith('//')) return trimmed;
  if (/^(https?:|den:)/i.test(trimmed)) return trimmed;
  return null;
}

function messageEvidenceLink(message: ChannelMessage): string {
  return safeEvidenceLink(message.deepLink) ?? `/api/gateway/messages/${message.id}`;
}

export function FocusedSessionView({ projectId, spaceName }: Props) {
  const [selectedLaneKey, setSelectedLaneKey] = useState('');
  const [senderIdentity, setSenderIdentity] = useState(readStoredSenderIdentity);
  const [targetMemberIdentity, setTargetMemberIdentity] = useState('');
  const [draft, setDraft] = useState('');
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<Error | null>(null);
  const transcriptRef = useRef<HTMLElement | null>(null);
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
  const { data: channels, loading: channelsLoading, error: channelsError, refresh: refreshChannels } = usePolling<Channel[]>(fetchChannels, 15000);

  const availableChannels = useMemo(
    () => (channels ?? []).filter(channel => channel.projectId === projectId),
    [channels, projectId],
  );

  const primaryChannel = useMemo(
    () => availableChannels.find(channel => channel.kind === 'project_default') ?? availableChannels[0] ?? null,
    [availableChannels],
  );

  const selectedChannelLane = useMemo(
    () => availableChannels.find(channel => laneKeyForChannel(channel) === selectedLaneKey) ?? null,
    [availableChannels, selectedLaneKey],
  );
  const activeChannel = selectedChannelLane ?? primaryChannel;

  const fetchSnapshots = useCallback(
    () => projectId ? listDesktopSessionSnapshots(projectId, { limit: 60 }) : Promise.resolve([]),
    [projectId],
  );
  const { data: snapshots, loading: snapshotsLoading, error: snapshotsError, refresh: refreshSnapshots } = usePolling<DesktopSessionSnapshot[]>(fetchSnapshots, 5000);

  const fetchMessages = useCallback(
    () => activeChannel ? listChannelMessages(activeChannel.id, { limit: 150 }) : Promise.resolve([]),
    [activeChannel],
  );
  const { data: messages, loading: messagesLoading, error: messagesError, refresh: refreshMessages } = usePolling<ChannelMessage[]>(fetchMessages, 3000);

  const sortedMessages = useMemo(() => {
    const visibleMessages = activeChannel
      ? (messages ?? []).filter(message => message.channelId === activeChannel.id)
      : [];
    return [...visibleMessages].sort((left, right) => left.id - right.id);
  }, [activeChannel, messages]);

  const selectedThreadId: number | null = null;

  const fetchMemberships = useCallback(
    () => activeChannel ? listGatewayMemberships({ channelId: activeChannel.id }) : Promise.resolve(null),
    [activeChannel],
  );
  const { data: memberships, loading: membershipsLoading, error: membershipsError, refresh: refreshMemberships } = usePolling<GatewayMemberships | null>(fetchMemberships, 5000);

  const members = useMemo(() => memberships?.members ?? [], [memberships]);
  const activeAgentMembers = useMemo(
    () => members.filter(member => member.memberType === 'agent' && member.membershipStatus === 'active'),
    [members],
  );

  const sortedSnapshots = useMemo(() => {
    return [...(snapshots ?? [])]
      .filter(snapshot => snapshot.project_id === projectId)
      .sort((left, right) => sessionActivityAt(right).localeCompare(sessionActivityAt(left)));
  }, [projectId, snapshots]);

  const liveSessionLanes = useMemo<FocusedSessionLane[]>(() => {
    if (!primaryChannel) return [];
    return sortedSnapshots.filter(isLiveSession).map(snapshot => {
      const state = deriveSessionState(snapshot, members);
      return {
        key: laneKeyForSession(snapshot),
        kind: 'session',
        channel: primaryChannel,
        snapshot,
        label: buildLaneLabel(primaryChannel, snapshot, state, selectedThreadId, projectId),
        state,
        lastActivityAt: sessionActivityAt(snapshot),
        threadId: selectedThreadId,
      };
    });
  }, [members, primaryChannel, projectId, selectedThreadId, sortedSnapshots]);

  const recentSessionLanes = useMemo<FocusedSessionLane[]>(() => {
    if (!primaryChannel) return [];
    const sessionLanes = sortedSnapshots.filter(snapshot => !isLiveSession(snapshot)).map(snapshot => {
      const state = deriveSessionState(snapshot, members);
      return {
        key: laneKeyForSession(snapshot),
        kind: 'session' as const,
        channel: primaryChannel,
        snapshot,
        label: buildLaneLabel(primaryChannel, snapshot, state, selectedThreadId, projectId),
        state,
        lastActivityAt: sessionActivityAt(snapshot),
        threadId: selectedThreadId,
      };
    });
    const channelLanes = availableChannels.map(channel => {
      const state = deriveSessionState(null, members);
      return {
        key: laneKeyForChannel(channel),
        kind: 'channel' as const,
        channel,
        snapshot: null,
        label: buildLaneLabel(channel, null, state, selectedThreadId, projectId),
        state,
        lastActivityAt: channel.updatedAt,
        threadId: selectedThreadId,
      };
    });
    return [...sessionLanes, ...channelLanes].sort((left, right) => right.lastActivityAt.localeCompare(left.lastActivityAt));
  }, [availableChannels, members, primaryChannel, projectId, selectedThreadId, sortedSnapshots]);

  const allLanes = useMemo(() => [...liveSessionLanes, ...recentSessionLanes], [liveSessionLanes, recentSessionLanes]);

  useEffect(() => {
    if (allLanes.length === 0) {
      setSelectedLaneKey('');
      return;
    }
    if (!selectedLaneKey || !allLanes.some(lane => lane.key === selectedLaneKey)) {
      setSelectedLaneKey(allLanes[0].key);
    }
  }, [allLanes, selectedLaneKey]);

  const selectedLane = useMemo(
    () => allLanes.find(lane => lane.key === selectedLaneKey) ?? allLanes[0] ?? null,
    [allLanes, selectedLaneKey],
  );
  const selectedSnapshot = selectedLane?.snapshot ?? null;

  const fetchEvents = useCallback(
    () => projectId && selectedSnapshot
      ? listDesktopSessionEvents(projectId, {
        sourceInstanceId: selectedSnapshot.source_instance_id,
        sessionId: selectedSnapshot.session_id,
        limit: 60,
      })
      : Promise.resolve([]),
    [projectId, selectedSnapshot],
  );
  const { data: sessionEvents, loading: eventsLoading, error: eventsError, refresh: refreshEvents } = usePolling<DesktopSessionEvent[]>(fetchEvents, 4000);

  const orderedEvents = useMemo(
    () => [...(sessionEvents ?? [])].sort((left, right) => right.created_at.localeCompare(left.created_at)),
    [sessionEvents],
  );
  const evidenceEvents = useMemo(
    () => orderedEvents.filter(event => !NOISY_EVENT_TYPES.has(event.event_type)).slice(0, 12),
    [orderedEvents],
  );

  useEffect(() => {
    if (activeAgentMembers.length === 0) {
      setTargetMemberIdentity('');
      return;
    }
    if (targetMemberIdentity && !activeAgentMembers.some(member => member.memberIdentity === targetMemberIdentity)) {
      setTargetMemberIdentity('');
    }
  }, [activeAgentMembers, targetMemberIdentity]);

  const conversationMessages = useMemo(
    () => sortedMessages.filter(message => !isCommandOrResultMessage(message)),
    [sortedMessages],
  );
  const workflowMessages = useMemo(
    () => sortedMessages.filter(isCommandOrResultMessage),
    [sortedMessages],
  );

  const selectedTarget = activeAgentMembers.find(member => member.memberIdentity === targetMemberIdentity) ?? null;
  const identityRequired = Boolean(projectId) && normalizedSenderIdentity.length === 0;
  const composerDisabled = !activeChannel || sending || identityRequired || Boolean(channelsError);
  const topError = sendError ?? channelsError ?? snapshotsError ?? messagesError ?? membershipsError ?? eventsError;
  const viewState = selectedLane?.state ?? 'queued';

  const handleSenderIdentityChange = useCallback((event: ChangeEvent<HTMLInputElement>) => {
    const value = event.target.value;
    setSenderIdentity(value);
    persistSenderIdentity(value);
  }, []);

  const refreshAll = useCallback(() => {
    refreshChannels();
    refreshSnapshots();
    refreshMessages();
    refreshMemberships();
    refreshEvents();
  }, [refreshChannels, refreshEvents, refreshMemberships, refreshMessages, refreshSnapshots]);

  const snapTranscriptToBottom = useCallback(() => {
    const transcript = transcriptRef.current;
    if (!transcript) return;
    transcript.scrollTo({ top: transcript.scrollHeight, behavior: 'auto' });
  }, []);

  const handleSubmit = useCallback(async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const body = draft.trim();
    if (!activeChannel || !body || !normalizedSenderIdentity || composerDisabled) return;

    setSending(true);
    setSendError(null);
    try {
      const metadata = {
        laneKind: selectedLane?.kind ?? 'channel',
        selectedLaneKey: selectedLane?.key ?? null,
        sessionId: selectedSnapshot?.session_id ?? null,
        sourceInstanceId: selectedSnapshot?.source_instance_id ?? null,
        workspaceId: selectedSnapshot?.workspace_id ?? null,
        taskId: selectedSnapshot?.task_id ?? null,
        deliveryMode: selectedTarget ? 'direct_agent_message' : 'channel_message',
        targetMemberIdentity: selectedTarget?.memberIdentity ?? null,
        slashCommand: body.startsWith('/') ? body.split(/\s+/, 1)[0] : null,
      };
      await postChannelMessage(activeChannel.id, {
        senderType: 'user',
        senderIdentity: normalizedSenderIdentity,
        messageKind: body.startsWith('/') ? 'command' : 'human_text',
        body,
        threadRootMessageId: selectedLane?.threadId ?? null,
        metadataJson: JSON.stringify(metadata),
      });
      setDraft('');
      refreshMessages();
    } catch (error) {
      setSendError(error instanceof Error ? error : new Error(String(error)));
    } finally {
      setSending(false);
    }
  }, [activeChannel, composerDisabled, draft, normalizedSenderIdentity, refreshMessages, selectedLane, selectedSnapshot, selectedTarget]);

  return (
    <section className="focused-session-view" aria-label="Focused durable channel sessions">
      <div className="focused-session-toolbar">
        <div className="focused-session-title">
          <span>Sessions</span>
          <strong>{selectedLane ? channelLabel(selectedLane.channel, projectId) : channelLabel(null, projectId)}</strong>
          <span className={`focused-session-state focused-session-state-${viewState}`}>{viewState}</span>
        </div>
        <label className="focused-session-selector-label" htmlFor="focused-session-selector">Session lane</label>
        <select
          id="focused-session-selector"
          className="focused-session-selector"
          value={selectedLane?.key ?? ''}
          onChange={event => setSelectedLaneKey(event.target.value)}
          disabled={allLanes.length === 0}
        >
          {allLanes.length === 0 ? (
            <option value="">{channelsLoading || snapshotsLoading ? 'Loading sessions…' : 'No sessions or channels'}</option>
          ) : (
            <>
              <optgroup label="Live sessions">
                {liveSessionLanes.length === 0 ? (
                  <option value="" disabled>No live sessions</option>
                ) : liveSessionLanes.map(lane => (
                  <option key={lane.key} value={lane.key}>{lane.label}</option>
                ))}
              </optgroup>
              <optgroup label="Recent sessions">
                {recentSessionLanes.map(lane => (
                  <option key={lane.key} value={lane.key}>{lane.label}</option>
                ))}
              </optgroup>
            </>
          )}
        </select>
        <button type="button" className="focused-session-refresh" onClick={refreshAll}>Refresh</button>
        <button
          type="button"
          className="focused-session-refresh focused-session-bottom"
          onClick={snapTranscriptToBottom}
          title="Snap the connected transcript to the newest messages"
        >
          Bottom
        </button>
      </div>

      {topError && <div className="focused-session-error">{topError.message}</div>}

      <div className="focused-session-grid">
        <main ref={transcriptRef} className="focused-session-transcript" aria-label="Connected transcript">
          <div className="focused-session-section-header">
            <h3>Connected transcript</h3>
            <span>{messagesLoading ? 'loading…' : `${sortedMessages.length} channel messages`}</span>
          </div>

          <section className="focused-session-conversation">
            <h4>Conversation</h4>
            {conversationMessages.length === 0 ? (
              <div className="focused-session-empty">No visible conversation messages yet.</div>
            ) : conversationMessages.map(message => {
              const evidence = directMessageEvidence(message);
              const link = messageEvidenceLink(message);
              return (
                <article key={message.id} className={`focused-session-message focused-session-message-${message.senderType}`}>
                  <div className="focused-session-message-meta">
                    <span>{displayTime(message.createdAt)}</span>
                    <strong>{messageSender(message)}</strong>
                    <span>{message.messageKind}</span>
                    {message.sourceKind && <span>{message.sourceKind}</span>}
                  </div>
                  <p>{message.body}</p>
                  {evidence && (
                    <div className="focused-session-evidence-inline">
                      <strong>{evidence.target ? `Direct agent target ${evidence.target}` : 'Direct agent target pending'}</strong>
                      <span>{evidence.status}</span>
                    </div>
                  )}
                  {link && <a href={link} target="_blank" rel="noreferrer">evidence</a>}
                </article>
              );
            })}
          </section>

          <section className="focused-session-workflow">
            <h4>Workflow evidence</h4>
            <div className="focused-session-subtitle">Command/result messages are separated from mirrored workflow events to suppress transcript noise.</div>
            {workflowMessages.length === 0 ? (
              <div className="focused-session-empty">No Command/result messages yet.</div>
            ) : workflowMessages.map(message => {
              const evidence = directMessageEvidence(message);
              return (
                <article key={message.id} className="focused-session-message focused-session-command-result">
                  <div className="focused-session-message-meta">
                    <span>{displayTime(message.createdAt)}</span>
                    <strong>Command/result</strong>
                    <span>{messageSender(message)}</span>
                    <span>{message.sourceKind ?? message.messageKind}</span>
                  </div>
                  <p>{message.body}</p>
                  {evidence && (
                    <div className="focused-session-evidence-inline">
                      <strong>{evidence.target ? `claimed/delivered/reply-posted for ${evidence.target}` : 'claimed/delivered/reply-posted evidence'}</strong>
                      <span>{evidence.status}</span>
                    </div>
                  )}
                </article>
              );
            })}
          </section>
        </main>

        <aside className="focused-session-sidebar" aria-label="Den context">
          <section className="focused-session-card">
            <h3>Den context</h3>
            <dl className="focused-session-context-list">
              <dt>Project</dt><dd>{projectId ?? 'Select a project'}</dd>
              <dt>Channel</dt><dd>{channelLabel(activeChannel ?? null, projectId)}</dd>
              <dt>Task</dt><dd>{selectedSnapshot?.task_id != null ? `#${selectedSnapshot.task_id}` : 'No task metadata'}</dd>
              <dt>Thread</dt><dd>{selectedLane?.threadId != null ? `#${selectedLane.threadId}` : 'No thread metadata'}</dd>
              <dt>Workspace</dt><dd>{selectedSnapshot?.workspace_id ?? 'No workspace metadata'}</dd>
              <dt>Session</dt><dd>{selectedSnapshot?.session_id ?? 'Channel-only lane'}</dd>
              <dt>Agent</dt><dd>{selectedSnapshot?.agent_identity ?? selectedTarget?.memberIdentity ?? 'No agent selected'}</dd>
              <dt>Model/Profile</dt><dd>{sessionModelProfile(selectedSnapshot) ?? 'Not reported'}</dd>
            </dl>
          </section>

          <section className="focused-session-card focused-session-status-card">
            <h3>Status evidence</h3>
            <div className={`focused-session-status-pill focused-session-state-${viewState}`}>{viewState}</div>
            <p>{statusDetail(selectedSnapshot)}</p>
            {selectedSnapshot?.warnings?.length ? (
              <ul className="focused-session-warning-list">
                {selectedSnapshot.warnings.slice(0, 4).map(warning => <li key={warning}>{warning}</li>)}
              </ul>
            ) : null}
            {selectedSnapshot?.is_stale && <div className="focused-session-warning">Stale binding warning: desktop snapshot is outside freshness bounds.</div>}
          </section>

          <section className="focused-session-card">
            <h3>Tool evidence</h3>
            <p>{currentToolLabel(selectedSnapshot, orderedEvents)}</p>
            <div className="focused-session-subtitle">{eventsLoading ? 'loading session events…' : `${evidenceEvents.length} bounded event previews`}</div>
            <div className="focused-session-event-list">
              {evidenceEvents.length === 0 ? (
                <div className="focused-session-empty">No status/tool events for this lane.</div>
              ) : evidenceEvents.map(event => (
                <div key={event.id} className="focused-session-event">
                  <span>{displayTime(event.created_at)}</span>
                  <strong>{event.event_type}</strong>
                  <p>{eventPayloadPreview(event)}</p>
                </div>
              ))}
            </div>
          </section>

          <section className="focused-session-card">
            <h3>Participants</h3>
            <div className="focused-session-subtitle">{membershipsLoading ? 'loading…' : `${activeAgentMembers.length} active agent(s)`}</div>
            <div className="focused-session-member-list">
              {members.length === 0 ? (
                <div className="focused-session-empty">No gateway membership metadata.</div>
              ) : members.map(member => (
                <button
                  key={member.id}
                  type="button"
                  className={`focused-session-member ${member.memberIdentity === targetMemberIdentity ? 'selected' : ''}`}
                  disabled={member.memberType !== 'agent' || member.membershipStatus !== 'active'}
                  onClick={() => setTargetMemberIdentity(member.memberIdentity)}
                >
                  <strong>{member.memberIdentity}</strong>
                  <span>{member.memberType} · {member.membershipStatus} · {member.wakePolicy}</span>
                  {member.settingsLabel && <span>{member.settingsLabel}</span>}
                </button>
              ))}
            </div>
          </section>
        </aside>
      </div>

      <form className="focused-session-composer" onSubmit={handleSubmit}>
        <label htmlFor="focused-session-sender">Posting as</label>
        <input
          id="focused-session-sender"
          value={senderIdentity}
          onChange={handleSenderIdentityChange}
          placeholder="your name"
          spellCheck={false}
          autoComplete="nickname"
        />
        <label htmlFor="focused-session-target">Direct agent target</label>
        <select
          id="focused-session-target"
          value={targetMemberIdentity}
          onChange={event => setTargetMemberIdentity(event.target.value)}
          disabled={activeAgentMembers.length === 0 || !activeChannel || sending}
        >
          <option value="">Channel lane</option>
          {activeAgentMembers.map(member => (
            <option key={member.id} value={member.memberIdentity}>@{member.memberIdentity}</option>
          ))}
        </select>
        <input
          className="focused-session-draft"
          value={draft}
          onChange={event => setDraft(event.target.value)}
          disabled={composerDisabled}
          placeholder={identityRequired ? 'Set Posting as before sending' : 'Message session lane or type /new'}
          aria-label="Focused session message"
        />
        <button type="submit" disabled={composerDisabled || draft.trim().length === 0}>{sending ? 'Sending…' : 'Send'}</button>
        <div className="focused-session-composer-help">Slash commands such as /new use postChannelMessage through the selected channel/session lane.</div>
      </form>
    </section>
  );
}
