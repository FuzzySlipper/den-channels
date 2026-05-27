import type {
  Project,
  Channel,
  ChannelMessage,
  ChannelReactionSummary,
  ChannelActivityEvent,
  ProjectWithStats,
  Space,
  TaskSummary,
  TaskDetail,
  ProjectTask,
  Message,
  Thread,
  DocumentSummary,
  Document,
  DocumentSearchResult,
  DocType,
  LibrarianResponse,
  AgentStreamEntry,
  AttentionItem,
  SubagentRunSummary,
  SubagentRunDetail,
  DispatchEntry,
  ReviewPacketResult,
  AgentWorkspace,
  GitStatusResponse,
  GitFilesResponse,
  GitDiffResponse,
  DesktopGitSnapshot,
  DesktopGitSnapshotLatestResult,
  DesktopDiffSnapshot,
  DesktopDiffSnapshotLatestResult,
  DesktopSessionSnapshot,
  DesktopSessionEvent,
  AppendDesktopSessionEventRequest,
  ListDesktopSessionEventsOptions,
  PostGatewayTestWakeRequest,
  PostGatewayDirectAgentMessageRequest,
  GatewayMemberships,
  GatewayTestWake,
  GatewayDirectAgentMessage,
  AgentsOverviewResponse,
  AgentDetailResponse,
} from './types';

const denCoreApiBase = normalizeApiBase(import.meta.env.VITE_DEN_CORE_API_BASE, '/den-core-api');
const denChannelsApiBase = normalizeApiBase(import.meta.env.VITE_DEN_CHANNELS_API_BASE, '/api');

function normalizeApiBase(value: string | undefined, fallback: string): string {
  const trimmed = (value ?? fallback).trim().replace(/\/+$/, '');
  return trimmed.length > 0 ? trimmed : fallback;
}

function apiUrl(base: string, url: string): string {
  if (/^https?:\/\//i.test(url)) {
    return url;
  }

  return `${base}${url.startsWith('/') ? url : `/${url}`}`;
}

function coreApiUrl(url: string): string {
  return apiUrl(denCoreApiBase, url);
}

function channelsApiUrl(url: string): string {
  return apiUrl(denChannelsApiBase, url);
}

async function get<T>(url: string): Promise<T> {
  const requestUrl = coreApiUrl(url);
  const res = await fetch(requestUrl);
  if (!res.ok) throw new Error(`GET ${requestUrl}: ${res.status}`);
  return res.json();
}

async function put<T>(url: string, body: unknown): Promise<T> {
  const requestUrl = coreApiUrl(url);
  const res = await fetch(requestUrl, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`PUT ${requestUrl}: ${res.status}`);
  return res.json();
}

async function post<T>(url: string, body: unknown): Promise<T> {
  const requestUrl = coreApiUrl(url);
  const res = await fetch(requestUrl, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`POST ${requestUrl}: ${res.status}`);
  return res.json();
}

async function getChannels<T>(url: string): Promise<T> {
  const requestUrl = channelsApiUrl(url);
  const res = await fetch(requestUrl);
  if (!res.ok) throw new Error(`GET ${requestUrl}: ${res.status}`);
  return res.json();
}

async function putChannels<T>(url: string, body: unknown): Promise<T> {
  const requestUrl = channelsApiUrl(url);
  const res = await fetch(requestUrl, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`PUT ${requestUrl}: ${res.status}`);
  return res.json();
}

async function postChannels<T>(url: string, body: unknown): Promise<T> {
  const requestUrl = channelsApiUrl(url);
  const res = await fetch(requestUrl, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`POST ${requestUrl}: ${res.status}`);
  return res.json();
}

function esc(s: string): string {
  return encodeURIComponent(s);
}

function buildQuery(params: Record<string, string | number | boolean | undefined | null>): string {
  const parts = Object.entries(params)
    .filter(([, v]) => v != null)
    .map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`);
  return parts.length > 0 ? `?${parts.join('&')}` : '';
}

// Spaces / Projects

export interface ListSpacesOpts {
  kind?: string;
  includeHidden?: boolean;
  includeArchived?: boolean;
}

export function listSpaces(opts: ListSpacesOpts = {}): Promise<Space[]> {
  const q = buildQuery({
    kind: opts.kind,
    includeHidden: opts.includeHidden,
    includeArchived: opts.includeArchived,
  });
  return get(`/api/spaces${q}`);
}

export function listProjects(): Promise<Project[]> {
  return get('/api/projects');
}

export function getProject(id: string, agent?: string): Promise<ProjectWithStats> {
  const q = buildQuery({ agent });
  return get(`/api/projects/${esc(id)}${q}`);
}

// Den Channels local API

export interface EnsureProjectDefaultChannelRequest {
  displayName?: string;
  createdBy?: string;
  settingsJson?: string | null;
}

export interface ListChannelsOpts {
  projectId?: string;
  kind?: string;
  limit?: number;
}

export interface ListChannelMessagesOpts {
  afterId?: number;
  limit?: number;
}

export interface ListChannelActivityEventsOpts {
  deliveryRequestId?: string;
  hermesSessionKey?: string;
  displayBlockId?: string;
  workerRunId?: string;
  anchorMessageId?: number;
  taskId?: number;
  afterId?: number;
  limit?: number;
}

export interface PostChannelMessageRequest {
  senderType: string;
  senderIdentity: string;
  body: string;
  messageKind?: string;
  sourceKind?: string | null;
  sourceId?: string | null;
  sourceProjectId?: string | null;
  summary?: string | null;
  deepLink?: string | null;
  threadRootMessageId?: number | null;
  replyToMessageId?: number | null;
  metadataJson?: string | null;
  deliveryRequestId?: string | null;
  dedupeKey?: string | null;
}

export interface AddChannelReactionRequest {
  reactorType: string;
  reactorIdentity: string;
  reactionKey: string;
}

export function listChannels(opts: ListChannelsOpts = {}): Promise<Channel[]> {
  const q = buildQuery({ projectId: opts.projectId, kind: opts.kind, limit: opts.limit });
  return getChannels(`/channels${q}`);
}

export function ensureProjectDefaultChannel(
  projectId: string,
  request: EnsureProjectDefaultChannelRequest = {},
): Promise<Channel> {
  return putChannels(`/projects/${esc(projectId)}/default-channel`, {
    displayName: request.displayName,
    createdBy: request.createdBy ?? 'den-web',
    settingsJson: request.settingsJson,
  });
}

export function ensureAgentCommonsChannel(): Promise<Channel> {
  return putChannels('/agent-commons', {});
}

export function listChannelMessages(channelId: number, opts: ListChannelMessagesOpts = {}): Promise<ChannelMessage[]> {
  const q = buildQuery({ afterId: opts.afterId, limit: opts.limit });
  return getChannels(`/channels/${channelId}/messages${q}`);
}

export function listChannelActivityEvents(channelId: number, opts: ListChannelActivityEventsOpts = {}): Promise<ChannelActivityEvent[]> {
  const q = buildQuery({
    deliveryRequestId: opts.deliveryRequestId,
    hermesSessionKey: opts.hermesSessionKey,
    displayBlockId: opts.displayBlockId,
    workerRunId: opts.workerRunId,
    anchorMessageId: opts.anchorMessageId,
    taskId: opts.taskId,
    afterId: opts.afterId,
    limit: opts.limit,
  });
  return getChannels(`/channels/${channelId}/activity-events${q}`);
}

export function postChannelMessage(channelId: number, request: PostChannelMessageRequest): Promise<ChannelMessage> {
  return postChannels(`/channels/${channelId}/messages`, request);
}

export function listChannelReactions(channelId: number): Promise<ChannelReactionSummary[]> {
  return getChannels(`/channels/${channelId}/reactions`);
}

export function addChannelReaction(messageId: number, request: AddChannelReactionRequest): Promise<unknown> {
  return postChannels(`/channel-messages/${messageId}/reactions`, request);
}

export interface UpsertChannelMembershipRequest {
  memberType: string;
  memberIdentity: string;
  membershipStatus?: string;
  wakePolicy?: string;
  canSend?: boolean;
  canReact?: boolean;
  canInvite?: boolean;
  cooldownSeconds?: number;
  maxAutoRepliesPerWindow?: number;
  settingsJson?: string | null;
}

export function upsertChannelMembership(channelId: number, request: UpsertChannelMembershipRequest): Promise<GatewayMemberships['members'][number]> {
  return putChannels(`/channels/${channelId}/memberships`, request);
}

export function listGatewayMemberships(opts: { channelId?: number; projectId?: string }): Promise<GatewayMemberships> {
  const q = buildQuery({ channelId: opts.channelId, projectId: opts.projectId });
  return getChannels(`/gateway/memberships${q}`);
}

export function postGatewayTestWake(request: PostGatewayTestWakeRequest): Promise<GatewayTestWake> {
  return postChannels('/gateway/test-wakes', request);
}

export function postGatewayDirectAgentMessage(request: PostGatewayDirectAgentMessageRequest): Promise<GatewayDirectAgentMessage> {
  return postChannels('/gateway/direct-agent-messages', request);
}

// Tasks

export interface ListTasksOpts {
  status?: string;
  assignedTo?: string;
  tags?: string;
  priority?: number;
  parentId?: number;
  tree?: boolean;
}

export function listTasks(projectId: string, opts: ListTasksOpts = {}): Promise<TaskSummary[]> {
  const q = buildQuery({
    status: opts.status,
    assignedTo: opts.assignedTo,
    tags: opts.tags,
    priority: opts.priority,
    parentId: opts.parentId,
    tree: opts.tree,
  });
  return get(`/api/projects/${esc(projectId)}/tasks${q}`);
}

export function getTask(projectId: string, taskId: number): Promise<TaskDetail> {
  return get(`/api/projects/${esc(projectId)}/tasks/${taskId}`);
}

export function updateTask(
  projectId: string,
  taskId: number,
  agent: string,
  changes: Record<string, unknown>,
): Promise<ProjectTask> {
  return put(`/api/projects/${esc(projectId)}/tasks/${taskId}`, { agent, ...changes });
}

export function requestReview(
  projectId: string,
  taskId: number,
  body: Record<string, unknown>,
): Promise<ReviewPacketResult> {
  return post(`/api/projects/${esc(projectId)}/tasks/${taskId}/review/request`, body);
}

export function postReviewFindings(
  projectId: string,
  taskId: number,
  body: Record<string, unknown>,
): Promise<ReviewPacketResult> {
  return post(`/api/projects/${esc(projectId)}/tasks/${taskId}/review/findings/post`, body);
}

export function getNextTask(projectId: string, assignedTo?: string): Promise<ProjectTask | null> {
  const q = buildQuery({ assignedTo });
  return get<ProjectTask | { message: string }>(`/api/projects/${esc(projectId)}/tasks/next${q}`)
    .then(res => ('message' in res ? null : res));
}

// Messages

export interface GetMessagesOpts {
  taskId?: number;
  since?: string;
  unreadFor?: string;
  limit?: number;
  intent?: string;
}

export function getMessage(projectId: string, messageId: number): Promise<Message | null> {
  return fetch(coreApiUrl(`/api/projects/${esc(projectId)}/messages/${messageId}`))
    .then(res => {
      if (res.status === 404) return null;
      if (!res.ok) throw new Error(`GET message: ${res.status}`);
      return res.json();
    });
}

export function getMessages(projectId: string, opts: GetMessagesOpts = {}): Promise<Message[]> {
  const q = buildQuery({
    taskId: opts.taskId,
    since: opts.since,
    unreadFor: opts.unreadFor,
    limit: opts.limit,
    intent: opts.intent,
  });
  return get(`/api/projects/${esc(projectId)}/messages${q}`);
}

export function getThread(projectId: string, threadId: number): Promise<Thread> {
  return get(`/api/projects/${esc(projectId)}/messages/thread/${threadId}`);
}

// Documents

export function listDocuments(projectId?: string, docType?: string, tags?: string): Promise<DocumentSummary[]> {
  if (projectId) {
    const q = buildQuery({ docType, tags });
    return get(`/api/projects/${esc(projectId)}/documents${q}`);
  }
  const q = buildQuery({ projectId, docType, tags });
  return get(`/api/documents${q}`);
}

export function getDocument(projectId: string, slug: string): Promise<Document | null> {
  return fetch(coreApiUrl(`/api/projects/${esc(projectId)}/documents/${esc(slug)}`))
    .then(res => {
      if (res.status === 404) return null;
      if (!res.ok) throw new Error(`GET document: ${res.status}`);
      return res.json();
    });
}

export interface SaveDocumentRequest {
  slug: string;
  title: string;
  content: string;
  doc_type?: DocType;
  tags?: string[] | null;
}

export function saveDocument(projectId: string, doc: SaveDocumentRequest): Promise<Document> {
  return post(`/api/projects/${esc(projectId)}/documents`, doc);
}

export function searchDocuments(query: string, projectId?: string): Promise<DocumentSearchResult[]> {
  if (projectId) {
    return get(`/api/projects/${esc(projectId)}/documents/search?query=${esc(query)}`);
  }
  const q = buildQuery({ query, projectId });
  return get(`/api/documents/search${q}`);
}

export interface QueryLibrarianRequest {
  query: string;
  taskId?: number;
  includeGlobal?: boolean;
}

export function queryLibrarian(projectId: string, request: QueryLibrarianRequest): Promise<LibrarianResponse> {
  return post(`/api/projects/${esc(projectId)}/librarian/query`, {
    query: request.query,
    task_id: request.taskId,
    include_global: request.includeGlobal ?? true,
  });
}

// Attention

export interface ListAttentionOpts {
  projectId?: string;
  taskId?: number;
  kind?: string;
  severity?: string;
  limit?: number;
}

export function listAttention(opts: ListAttentionOpts = {}): Promise<AttentionItem[]> {
  const q = buildQuery({
    projectId: opts.projectId,
    taskId: opts.taskId,
    kind: opts.kind,
    severity: opts.severity,
    limit: opts.limit,
  });
  return get(`/api/attention${q}`);
}

export function listProjectAttention(projectId: string, opts: Omit<ListAttentionOpts, 'projectId'> = {}): Promise<AttentionItem[]> {
  const q = buildQuery({
    taskId: opts.taskId,
    kind: opts.kind,
    severity: opts.severity,
    limit: opts.limit,
  });
  return get(`/api/projects/${esc(projectId)}/attention${q}`);
}

// Agent stream

export interface ListAgentStreamOpts {
  projectId?: string;
  taskId?: number;
  dispatchId?: number;
  streamKind?: string;
  eventType?: string;
  sender?: string;
  senderInstanceId?: string;
  recipientAgent?: string;
  recipientRole?: string;
  recipientInstanceId?: string;
  metadataRunId?: string;
  limit?: number;
}

export function listAgentStream(opts: ListAgentStreamOpts = {}): Promise<AgentStreamEntry[]> {
  const q = buildQuery({
    projectId: opts.projectId,
    taskId: opts.taskId,
    dispatchId: opts.dispatchId,
    streamKind: opts.streamKind,
    eventType: opts.eventType,
    sender: opts.sender,
    senderInstanceId: opts.senderInstanceId,
    recipientAgent: opts.recipientAgent,
    recipientRole: opts.recipientRole,
    recipientInstanceId: opts.recipientInstanceId,
    metadataRunId: opts.metadataRunId,
    limit: opts.limit,
  });
  return get(`/api/agent-stream${q}`);
}

export interface ListSubagentRunsOpts {
  projectId?: string;
  taskId?: number;
  state?: string;
  limit?: number;
}

export function listSubagentRuns(opts: ListSubagentRunsOpts = {}): Promise<SubagentRunSummary[]> {
  const q = buildQuery({
    projectId: opts.projectId,
    taskId: opts.taskId,
    state: opts.state,
    limit: opts.limit,
  });
  return get(`/api/subagent-runs${q}`);
}

export function getSubagentRun(runId: string, opts: Omit<ListSubagentRunsOpts, 'limit'> = {}): Promise<SubagentRunDetail> {
  const q = buildQuery({
    projectId: opts.projectId,
    taskId: opts.taskId,
  });
  return get(`/api/subagent-runs/${esc(runId)}${q}`);
}

export type SubagentRunControlAction = 'abort' | 'rerun';

export interface ControlSubagentRunOpts extends Omit<ListSubagentRunsOpts, 'state' | 'limit'> {
  action: SubagentRunControlAction;
  requestedBy?: string;
  reason?: string;
}

export function controlSubagentRun(runId: string, opts: ControlSubagentRunOpts): Promise<AgentStreamEntry> {
  const q = buildQuery({
    projectId: opts.projectId,
    taskId: opts.taskId,
  });
  return post(`/api/subagent-runs/${esc(runId)}/control${q}`, {
    action: opts.action,
    requested_by: opts.requestedBy ?? 'web-ui',
    reason: opts.reason,
  });
}

// Git inspection

export interface ListAgentWorkspacesOpts {
  taskId?: number;
  state?: string;
  limit?: number;
}

export function listProjectAgentWorkspaces(projectId: string, opts: ListAgentWorkspacesOpts = {}): Promise<AgentWorkspace[]> {
  const q = buildQuery({
    taskId: opts.taskId,
    state: opts.state,
    limit: opts.limit,
  });
  return get(`/api/projects/${esc(projectId)}/agent-workspaces${q}`);
}

export function getProjectGitStatus(projectId: string): Promise<GitStatusResponse> {
  return get(`/api/projects/${esc(projectId)}/git/status`);
}

export interface GitFilesOpts {
  baseRef?: string;
  headRef?: string;
  includeUntracked?: boolean;
}

export function getProjectGitFiles(projectId: string, opts: GitFilesOpts = {}): Promise<GitFilesResponse> {
  const q = buildQuery({
    baseRef: opts.baseRef,
    headRef: opts.headRef,
    includeUntracked: opts.includeUntracked,
  });
  return get(`/api/projects/${esc(projectId)}/git/files${q}`);
}

export interface GitDiffOpts {
  path?: string;
  baseRef?: string;
  headRef?: string;
  maxBytes?: number;
  staged?: boolean;
}

export function getProjectGitDiff(projectId: string, opts: GitDiffOpts = {}): Promise<GitDiffResponse> {
  const q = buildQuery({
    path: opts.path,
    baseRef: opts.baseRef,
    headRef: opts.headRef,
    maxBytes: opts.maxBytes,
    staged: opts.staged,
  });
  return get(`/api/projects/${esc(projectId)}/git/diff${q}`);
}

export function getWorkspaceGitStatus(projectId: string, workspaceId: string): Promise<GitStatusResponse> {
  return get(`/api/projects/${esc(projectId)}/agent-workspaces/${esc(workspaceId)}/git/status`);
}

export function getWorkspaceGitFiles(projectId: string, workspaceId: string, opts: GitFilesOpts = {}): Promise<GitFilesResponse> {
  const q = buildQuery({
    baseRef: opts.baseRef,
    headRef: opts.headRef,
    includeUntracked: opts.includeUntracked,
  });
  return get(`/api/projects/${esc(projectId)}/agent-workspaces/${esc(workspaceId)}/git/files${q}`);
}

export function getWorkspaceGitDiff(projectId: string, workspaceId: string, opts: GitDiffOpts = {}): Promise<GitDiffResponse> {
  const q = buildQuery({
    path: opts.path,
    baseRef: opts.baseRef,
    headRef: opts.headRef,
    maxBytes: opts.maxBytes,
    staged: opts.staged,
  });
  return get(`/api/projects/${esc(projectId)}/agent-workspaces/${esc(workspaceId)}/git/diff${q}`);
}

// Desktop-published snapshots

export interface ListDesktopSnapshotsOpts {
  taskId?: number;
  workspaceId?: string;
  sourceInstanceId?: string;
  rootPath?: string;
  state?: string;
  staleAfterSeconds?: number;
  limit?: number;
}

export function listDesktopGitSnapshots(projectId: string, opts: ListDesktopSnapshotsOpts = {}): Promise<DesktopGitSnapshot[]> {
  const q = buildQuery({
    taskId: opts.taskId,
    workspaceId: opts.workspaceId,
    sourceInstanceId: opts.sourceInstanceId,
    rootPath: opts.rootPath,
    state: opts.state,
    staleAfterSeconds: opts.staleAfterSeconds,
    limit: opts.limit,
  });
  return get(`/api/projects/${esc(projectId)}/desktop/git-snapshots${q}`);
}

export function getLatestDesktopGitSnapshot(projectId: string, opts: Omit<ListDesktopSnapshotsOpts, 'state' | 'limit'> = {}): Promise<DesktopGitSnapshotLatestResult> {
  const q = buildQuery({
    taskId: opts.taskId,
    workspaceId: opts.workspaceId,
    sourceInstanceId: opts.sourceInstanceId,
    rootPath: opts.rootPath,
    staleAfterSeconds: opts.staleAfterSeconds,
  });
  return get(`/api/projects/${esc(projectId)}/desktop/git-snapshots/latest${q}`);
}

export interface DesktopDiffSnapshotOpts extends Omit<ListDesktopSnapshotsOpts, 'state' | 'limit'> {
  path?: string;
  baseRef?: string;
  headRef?: string;
  staged?: boolean;
}

export function getLatestDesktopDiffSnapshot(projectId: string, opts: DesktopDiffSnapshotOpts = {}): Promise<DesktopDiffSnapshotLatestResult> {
  const q = buildQuery({
    taskId: opts.taskId,
    workspaceId: opts.workspaceId,
    sourceInstanceId: opts.sourceInstanceId,
    rootPath: opts.rootPath,
    path: opts.path,
    baseRef: opts.baseRef,
    headRef: opts.headRef,
    staged: opts.staged,
    staleAfterSeconds: opts.staleAfterSeconds,
  });
  return get(`/api/projects/${esc(projectId)}/desktop/diff-snapshots/latest${q}`);
}

export interface ListDesktopSessionSnapshotsOpts extends Omit<ListDesktopSnapshotsOpts, 'rootPath' | 'state'> {
  sessionId?: string;
}

export function listDesktopSessionSnapshots(projectId: string, opts: ListDesktopSessionSnapshotsOpts = {}): Promise<DesktopSessionSnapshot[]> {
  const q = buildQuery({
    taskId: opts.taskId,
    workspaceId: opts.workspaceId,
    sourceInstanceId: opts.sourceInstanceId,
    sessionId: opts.sessionId,
    staleAfterSeconds: opts.staleAfterSeconds,
    limit: opts.limit,
  });
  return get(`/api/projects/${esc(projectId)}/desktop/session-snapshots${q}`);
}

export function listDesktopSessionEvents(projectId: string, opts: ListDesktopSessionEventsOptions = {}): Promise<DesktopSessionEvent[]> {
  const eventTypes = Array.isArray(opts.eventTypes) ? opts.eventTypes.join(',') : opts.eventTypes;
  const q = buildQuery({
    taskId: opts.taskId,
    workspaceId: opts.workspaceId,
    sourceInstanceId: opts.sourceInstanceId,
    sessionId: opts.sessionId,
    eventTypes,
    limit: opts.limit,
  });
  return get(`/api/projects/${esc(projectId)}/desktop/session-events${q}`);
}

export function appendDesktopSessionEvent(
  projectId: string,
  event: AppendDesktopSessionEventRequest,
): Promise<DesktopSessionEvent> {
  return post(`/api/projects/${esc(projectId)}/desktop/session-events`, event);
}

export type {
  DesktopGitSnapshot,
  DesktopDiffSnapshot,
  DesktopSessionSnapshot,
  DesktopSessionEvent,
  AppendDesktopSessionEventRequest,
  ListDesktopSessionEventsOptions,
};

// Legacy dispatch helpers.
// The default dashboard intentionally does not import these; keep them available
// for historical dispatch detail links or a future explicit legacy/debug view.

export interface ListDispatchesOpts {
  projectId?: string;
  targetAgent?: string;
  status?: string;
}

export function listDispatches(opts: ListDispatchesOpts = {}): Promise<DispatchEntry[]> {
  const q = buildQuery({
    projectId: opts.projectId,
    targetAgent: opts.targetAgent,
    status: opts.status,
  });
  return get(`/api/dispatch${q}`);
}

export function getDispatch(dispatchId: number): Promise<DispatchEntry> {
  return get(`/api/dispatch/${dispatchId}`);
}

export function approveDispatch(dispatchId: number, decidedBy: string): Promise<DispatchEntry> {
  return post(`/api/dispatch/${dispatchId}/approve`, { decided_by: decidedBy });
}

export function rejectDispatch(dispatchId: number, decidedBy: string): Promise<DispatchEntry> {
  return post(`/api/dispatch/${dispatchId}/reject`, { decided_by: decidedBy });
}

// Agents Overview API (#1694 / #1695)

export interface ListAgentsOverviewOpts {
  projectId?: string;
  channelId?: string;
  scope?: string;
  agentIdentity?: string;
  activityLimit?: number;
  includeLeft?: boolean;
  includeGateway?: boolean;
}

export function listAgentsOverview(opts: ListAgentsOverviewOpts = {}): Promise<AgentsOverviewResponse> {
  const q = buildQuery({
    projectId: opts.projectId,
    channelId: opts.channelId,
    scope: opts.scope,
    agentIdentity: opts.agentIdentity,
    activityLimit: opts.activityLimit,
    includeLeft: opts.includeLeft,
    includeGateway: opts.includeGateway,
  });
  return getChannels(`/agents/overview${q}`);
}

export function getAgentDetail(agentIdentity: string, opts: { projectId?: string; channelId?: string; activityLimit?: number; deliveryLimit?: number } = {}): Promise<AgentDetailResponse> {
  const q = buildQuery({
    projectId: opts.projectId,
    channelId: opts.channelId,
    activityLimit: opts.activityLimit,
    deliveryLimit: opts.deliveryLimit,
  });
  return getChannels(`/agents/${encodeURIComponent(agentIdentity)}/overview${q}`);
}
