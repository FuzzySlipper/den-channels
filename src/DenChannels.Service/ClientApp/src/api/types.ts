export type TaskStatus = 'planned' | 'in_progress' | 'review' | 'blocked' | 'done' | 'cancelled';
export type DocType = 'prd' | 'spec' | 'adr' | 'convention' | 'reference' | 'note';
export type MessageIntent =
  | 'general'
  | 'note'
  | 'status_update'
  | 'question'
  | 'answer'
  | 'handoff'
  | 'review_request'
  | 'review_feedback'
  | 'review_approval'
  | 'task_ready'
  | 'task_blocked'
  | 'notification';
export type AgentStreamKind = 'ops' | 'message';
export type AgentStreamDeliveryMode = 'record_only' | 'notify' | 'wake';
export type DispatchStatus = 'pending' | 'approved' | 'rejected' | 'completed' | 'expired';
export type DispatchTriggerType = 'message' | 'task_status';
export type ReviewVerdict = 'changes_requested' | 'looks_good' | 'follow_up_needed' | 'blocked_by_dependency';
export type ReviewFindingCategory = 'blocking_bug' | 'acceptance_gap' | 'test_weakness' | 'follow_up_candidate';
export type ReviewFindingStatus = 'open' | 'claimed_fixed' | 'verified_fixed' | 'not_fixed' | 'superseded' | 'split_to_follow_up';
export type ReviewPacketKind = 'review_request' | 'rereview_request' | 'review_findings';
export type AgentWorkspaceState = 'planned' | 'active' | 'review' | 'complete' | 'failed' | 'archived';
export type AgentWorkspaceCleanupPolicy = 'keep' | 'delete_worktree' | 'archive';
export type DesktopSessionEventType =
  | 'created'
  | 'discovered'
  | 'status_changed'
  | 'capabilities_changed'
  | 'attached'
  | 'detached'
  | 'input_sent'
  | 'resize_requested'
  | 'terminate_requested'
  | 'terminate_completed'
  | 'reconnect' // Legacy v1 catch-all; prefer reconnect_requested/reconnected for new integrations.
  | 'reconnect_requested'
  | 'reconnected'
  | 'lease_acquired'
  | 'lease_lost'
  | 'lease_conflict'
  | 'warning'
  | 'crashed'
  | 'exited'
  | 'snapshot_published'
  | 'snapshot_publish_failed';

export type SpaceKind = 'project' | 'personal' | 'assistant' | 'knowledge_base' | 'system' | string;
export type SpaceVisibility = 'normal' | 'hidden' | 'archived' | string;

export interface Space {
  id: string;
  name: string;
  kind: SpaceKind;
  visibility: SpaceVisibility;
  owner: string | null;
  root_path: string | null;
  description: string | null;
  created_at: string | null;
  updated_at: string | null;
}

export interface Project {
  id: string;
  name: string;
  kind?: SpaceKind;
  visibility?: SpaceVisibility;
  owner?: string | null;
  root_path: string | null;
  description: string | null;
  created_at: string | null;
  updated_at: string | null;
}

export interface Channel {
  id: number;
  slug: string;
  displayName: string;
  kind: string;
  projectId: string | null;
  spaceId: string | null;
  createdBy: string;
  visibility: string;
  settingsJson: string | null;
  createdAt: string;
  updatedAt: string;
  archivedAt: string | null;
}

export interface ChannelMessage {
  id: number;
  channelId: number;
  senderType: string;
  senderIdentity: string;
  body: string;
  messageKind: string;
  sourceKind: string | null;
  sourceId: string | null;
  sourceProjectId: string | null;
  summary: string | null;
  deepLink: string | null;
  threadRootMessageId: number | null;
  replyToMessageId: number | null;
  metadataJson: string | null;
  deliveryRequestId: string | null;
  dedupeKey: string | null;
  finalChannelMessageId: number | null;
  createdAt: string;
  editedAt: string | null;
  deletedAt: string | null;
}

export interface ChannelReactionSummary {
  channelMessageId: number;
  reactionKey: string;
  count: number;
  reactors: string[];
}

export interface ChannelActivityEvent {
  id: number;
  channelId: number;
  projectId: string | null;
  agentIdentity: string;
  deliveryRequestId: string | null;
  hermesSessionKey: string | null;
  displayBlockId: string | null;
  parentHermesSessionKey: string | null;
  parentAgentIdentity: string | null;
  workerRunId: string | null;
  workerRole: string | null;
  taskId: number | null;
  threadId: number | null;
  anchorMessageId: number | null;
  eventType: string;
  status: string;
  deliveryStage: string;
  terminal: boolean;
  sequence: number;
  updateVersion: number;
  title: string | null;
  summary: string | null;
  previewJson: string | null;
  metadataJson: string | null;
  dedupeKey: string | null;
  finalChannelMessageId: number | null;
  createdAt: string;
  updatedAt: string;
}

export type ChannelWakePolicy =
  | 'never'
  | 'mentions_only'
  | 'direct_questions_only'
  | 'substantive_digest'
  | 'all_human_messages'
  | 'all_messages_except_self';

export interface GatewayMember {
  id: number;
  memberType: string;
  memberIdentity: string;
  membershipStatus: string;
  wakePolicy: ChannelWakePolicy | string;
  canSend: boolean;
  canReact: boolean;
  canInvite: boolean;
  cooldownSeconds: number;
  maxAutoRepliesPerWindow: number;
  settingsLabel: string | null;
}

export interface GatewayMemberships {
  channelId: number;
  channelSlug: string;
  channelKind: string;
  projectId: string | null;
  members: GatewayMember[];
}

export interface PostGatewayTestWakeRequest {
  channelId?: number;
  projectId?: string;
  memberIdentity: string;
  requestedBy?: string;
  note?: string;
}

export interface GatewayTestWake {
  status: string;
  memberIdentity: string;
  wakePolicy: string;
  messageId: number;
  channelId: number;
  gatewayMessageUrl: string;
  gatewayEventsUrl: string;
  evidenceSummary: string;
}

export interface PostGatewayDirectAgentMessageRequest {
  channelId?: number;
  projectId?: string;
  memberIdentity: string;
  senderIdentity: string;
  body: string;
}

export interface GatewayDirectAgentMessage {
  status: string;
  deliveryStatus: string;
  claimStatus: string;
  completionStatus: string;
  suppressionStatus: string;
  memberIdentity: string;
  wakePolicy: string;
  messageId: number;
  channelId: number;
  requestId: string;
  gatewayMessageUrl: string;
  gatewayEventsUrl: string;
  evidenceSummary: string;
}

export interface ProjectWithStats {
  project: Project;
  task_counts_by_status: Record<string, number>;
  unread_message_count: number;
}

export interface ProjectTask {
  id: number;
  project_id: string;
  title: string;
  description: string | null;
  status: TaskStatus;
  priority: number;
  assigned_to: string | null;
  parent_id: number | null;
  tags: string[] | null;
  created_at: string;
  updated_at: string;
}

export interface TaskSummary {
  id: number;
  project_id: string;
  title: string;
  status: TaskStatus;
  priority: number;
  assigned_to: string | null;
  parent_id: number | null;
  tags: string[] | null;
  dependency_count: number;
  subtask_count: number;
}

export interface TaskDependencyInfo {
  task_id: number;
  title: string;
  status: TaskStatus;
}

export interface TaskDetail {
  task: ProjectTask;
  dependencies: TaskDependencyInfo[];
  subtasks: TaskSummary[];
  recent_messages: Message[];
  review_rounds: ReviewRound[];
  open_review_findings: ReviewFinding[];
  resolved_review_findings: ReviewFinding[];
  review_workflow: ReviewWorkflowSummary;
}

export interface ReviewRound {
  id: number;
  task_id: number;
  round_number: number;
  requested_by: string;
  branch: string;
  base_branch: string;
  base_commit: string;
  head_commit: string;
  last_reviewed_head_commit: string | null;
  commits_since_last_review: number | null;
  tests_run: string[] | null;
  notes: string | null;
  preferred_diff_base_ref: string | null;
  preferred_diff_base_commit: string | null;
  preferred_diff_head_ref: string | null;
  preferred_diff_head_commit: string | null;
  alternate_diff_base_ref: string | null;
  alternate_diff_base_commit: string | null;
  alternate_diff_head_ref: string | null;
  alternate_diff_head_commit: string | null;
  delta_base_commit: string | null;
  inherited_commit_count: number | null;
  task_local_commit_count: number | null;
  verdict: ReviewVerdict | null;
  verdict_by: string | null;
  verdict_notes: string | null;
  requested_at: string;
  verdict_at: string | null;
  preferred_diff: ReviewDiffRange;
  alternate_diff: ReviewDiffRange | null;
  delta_diff: ReviewDiffRange | null;
  branch_composition: ReviewBranchComposition;
  is_stacked_branch_review: boolean;
}

export interface ReviewDiffRange {
  base_ref: string;
  base_commit: string | null;
  head_ref: string;
  head_commit: string;
}

export interface ReviewBranchComposition {
  inherited_commit_count: number | null;
  task_local_commit_count: number | null;
  has_inherited_changes: boolean | null;
  has_task_local_changes: boolean | null;
}

export interface ReviewFinding {
  id: number;
  finding_key: string;
  task_id: number;
  review_round_id: number;
  review_round_number: number;
  finding_number: number;
  created_by: string;
  category: ReviewFindingCategory;
  summary: string;
  notes: string | null;
  file_references: string[] | null;
  test_commands: string[] | null;
  status: ReviewFindingStatus;
  status_updated_by: string | null;
  status_notes: string | null;
  status_updated_at: string | null;
  response_by: string | null;
  response_notes: string | null;
  response_at: string | null;
  follow_up_task_id: number | null;
  created_at: string;
  updated_at: string;
}

export interface ReviewWorkflowSummary {
  current_round: ReviewRound | null;
  current_verdict: ReviewVerdict | null;
  review_round_count: number;
  unresolved_finding_count: number;
  resolved_finding_count: number;
  addressed_finding_count: number;
  timeline: ReviewTimelineEntry[];
}

export interface ReviewTimelineEntry {
  review_round_id: number;
  review_round_number: number;
  branch: string;
  requested_by: string;
  requested_at: string;
  head_commit: string | null;
  last_reviewed_head_commit: string | null;
  commits_since_last_review: number | null;
  verdict: ReviewVerdict | null;
  verdict_by: string | null;
  verdict_at: string | null;
  total_finding_count: number;
  open_finding_count: number;
  addressed_finding_count: number;
  claimed_fixed_finding_count: number;
  resolved_finding_count: number;
}

export interface ReviewPacket {
  kind: ReviewPacketKind;
  title: string;
  content: string;
}

export interface ReviewPacketResult {
  review_round: ReviewRound | null;
  message: Message;
  packet: ReviewPacket;
  findings_addressed: string[];
  open_findings: string[];
  test_commands: string[];
}

export interface AgentWorkspace {
  id: string;
  project_id: string;
  task_id: number;
  branch: string;
  worktree_path: string;
  base_branch: string;
  base_commit: string | null;
  head_commit: string | null;
  state: AgentWorkspaceState;
  created_by_run_id: string | null;
  dev_server_url: string | null;
  preview_url: string | null;
  cleanup_policy: AgentWorkspaceCleanupPolicy;
  changed_file_summary: unknown | null;
  created_at: string;
  updated_at: string;
}

export interface GitDirtyCounts {
  total: number;
  staged: number;
  unstaged: number;
  untracked: number;
  modified: number;
  added: number;
  deleted: number;
  renamed: number;
}

export interface GitFileStatus {
  path: string;
  old_path: string | null;
  index_status: string | null;
  worktree_status: string | null;
  category: string;
  is_untracked: boolean;
}

export interface GitStatusResponse {
  project_id: string;
  workspace_id: string | null;
  task_id: number | null;
  workspace_branch: string | null;
  workspace_base_branch: string | null;
  workspace_base_commit: string | null;
  workspace_head_commit: string | null;
  root_path: string;
  is_git_repository: boolean;
  branch: string | null;
  is_detached: boolean;
  head_sha: string | null;
  upstream: string | null;
  ahead: number | null;
  behind: number | null;
  dirty_counts: GitDirtyCounts;
  files: GitFileStatus[];
  warnings: string[];
  errors: string[];
  truncated: boolean;
}

export interface GitFilesResponse {
  project_id: string;
  workspace_id: string | null;
  task_id: number | null;
  workspace_branch: string | null;
  workspace_base_branch: string | null;
  workspace_base_commit: string | null;
  workspace_head_commit: string | null;
  root_path: string;
  base_ref: string | null;
  head_ref: string | null;
  files: GitFileStatus[];
  warnings: string[];
  errors: string[];
  truncated: boolean;
}

export interface GitDiffResponse {
  project_id: string;
  workspace_id: string | null;
  task_id: number | null;
  workspace_branch: string | null;
  workspace_base_branch: string | null;
  workspace_base_commit: string | null;
  workspace_head_commit: string | null;
  root_path: string;
  path: string | null;
  base_ref: string | null;
  head_ref: string | null;
  max_bytes: number;
  staged: boolean;
  diff: string;
  truncated: boolean;
  binary: boolean;
  warnings: string[];
  errors: string[];
}

export type DesktopSnapshotState =
  | 'ok'
  | 'path_not_visible'
  | 'not_git_repository'
  | 'git_error'
  | 'source_offline'
  | 'missing';

export interface DesktopGitSnapshot {
  id: number;
  project_id: string;
  task_id: number | null;
  workspace_id: string | null;
  root_path: string;
  state: DesktopSnapshotState;
  branch: string | null;
  is_detached: boolean;
  head_sha: string | null;
  upstream: string | null;
  ahead: number | null;
  behind: number | null;
  dirty_counts: GitDirtyCounts;
  changed_files: GitFileStatus[];
  warnings: string[];
  truncated: boolean;
  source_instance_id: string;
  source_display_name: string | null;
  observed_at: string;
  received_at: string;
  updated_at: string;
  is_stale: boolean;
  freshness_seconds: number;
  freshness_status: string;
}

export interface DesktopGitSnapshotLatestResult {
  project_id: string;
  task_id: number | null;
  workspace_id: string | null;
  root_path: string | null;
  source_instance_id: string | null;
  state: DesktopSnapshotState;
  is_stale: boolean;
  freshness_status: string;
  snapshot: DesktopGitSnapshot | null;
}

export interface DesktopDiffSnapshot {
  id: number;
  project_id: string;
  task_id: number | null;
  workspace_id: string | null;
  root_path: string;
  path: string | null;
  base_ref: string | null;
  head_ref: string | null;
  max_bytes: number;
  staged: boolean;
  diff: string;
  truncated: boolean;
  binary: boolean;
  warnings: string[];
  source_instance_id: string;
  source_display_name: string | null;
  observed_at: string;
  received_at: string;
  updated_at: string;
  is_stale: boolean;
  freshness_seconds: number;
}

export interface DesktopDiffSnapshotLatestResult {
  project_id: string;
  task_id: number | null;
  workspace_id: string | null;
  root_path: string | null;
  path: string | null;
  source_instance_id: string | null;
  state: DesktopSnapshotState;
  is_stale: boolean;
  freshness_status: string;
  snapshot: DesktopDiffSnapshot | null;
}

export interface DesktopSessionSnapshot {
  id: number;
  project_id: string;
  task_id: number | null;
  workspace_id: string | null;
  session_id: string;
  parent_session_id: string | null;
  agent_identity: string | null;
  role: string | null;
  current_command: string | null;
  current_phase: string | null;
  title: string | null;
  display_name: string | null;
  cwd: string | null;
  kind: string | null;
  backend: string | null;
  status: string | null;
  started_at: string | null;
  last_activity_at: string | null;
  exited_at: string | null;
  exit_code: number | null;
  source_display_name: string | null;
  capabilities: unknown | null;
  recent_activity: unknown | null;
  child_sessions: unknown | null;
  control_capabilities: unknown | null;
  warnings: string[];
  source_instance_id: string;
  observed_at: string;
  received_at: string;
  updated_at: string;
  is_stale: boolean;
  freshness_seconds: number;
}

export interface DesktopSessionEvent {
  id: number;
  project_id: string;
  task_id: number | null;
  workspace_id: string | null;
  source_instance_id: string;
  session_id: string;
  event_type: DesktopSessionEventType;
  payload: string | null;
  requested_by: string | null;
  reason: string | null;
  observed_at: string;
  created_at: string;
}

export interface AppendDesktopSessionEventRequest {
  task_id?: number | null;
  workspace_id?: string | null;
  source_instance_id: string;
  session_id: string;
  event_type: DesktopSessionEventType;
  payload?: string | null;
  requested_by?: string | null;
  reason?: string | null;
  observed_at?: string | null;
}

export interface ListDesktopSessionEventsOptions {
  taskId?: number;
  workspaceId?: string;
  sourceInstanceId?: string;
  sessionId?: string;
  eventTypes?: DesktopSessionEventType[] | string;
  limit?: number;
}

export interface Message {
  id: number;
  project_id: string;
  task_id: number | null;
  thread_id: number | null;
  sender: string;
  content: string;
  intent: MessageIntent;
  metadata: unknown | null;
  created_at: string;
}

export interface Thread {
  root: Message;
  replies: Message[];
}

export interface Document {
  id: number;
  project_id: string;
  slug: string;
  title: string;
  content: string;
  doc_type: DocType;
  tags: string[] | null;
  created_at: string;
  updated_at: string;
}

export interface DocumentSummary {
  id: number;
  project_id: string;
  slug: string;
  title: string;
  doc_type: DocType;
  tags: string[] | null;
  updated_at: string;
}

export interface DocumentSearchResult {
  project_id: string;
  slug: string;
  title: string;
  doc_type: DocType;
  snippet: string;
  rank: number;
}

export type LibrarianConfidence = 'low' | 'medium' | 'high';

export interface RelevantItem {
  type: string;
  source_id: string;
  project_id: string | null;
  summary: string;
  why_relevant: string;
  snippet: string | null;
}

export interface LibrarianResponse {
  relevant_items: RelevantItem[];
  recommendations: string[];
  confidence: LibrarianConfidence;
}

export interface AgentStreamEntry {
  id: number;
  stream_kind: AgentStreamKind;
  event_type: string;
  project_id: string | null;
  task_id: number | null;
  thread_id: number | null;
  dispatch_id: number | null;
  sender: string;
  sender_instance_id: string | null;
  recipient_agent: string | null;
  recipient_role: string | null;
  recipient_instance_id: string | null;
  delivery_mode: AgentStreamDeliveryMode;
  body: string | null;
  metadata: Record<string, unknown> | null;
  dedup_key: string | null;
  created_at: string;
}

export type SubagentRunState =
  | 'running'
  | 'retrying'
  | 'aborting'
  | 'rerun_requested'
  | 'rerun_accepted'
  | 'complete'
  | 'failed'
  | 'timeout'
  | 'aborted'
  | 'unknown';

export interface SubagentRunSummary {
  run_id: string;
  state: SubagentRunState;
  schema: string | null;
  schema_version: number | null;
  latest: AgentStreamEntry;
  started: AgentStreamEntry | null;
  role: string | null;
  task_id: number | null;
  project_id: string | null;
  backend: string | null;
  model: string | null;
  review_round_id: number | null;
  workspace_id: string | null;
  purpose: string | null;
  worktree_path: string | null;
  branch: string | null;
  base_branch: string | null;
  base_commit: string | null;
  head_commit: string | null;
  final_head_commit: string | null;
  final_head_status: string | null;
  started_at: string | null;
  ended_at: string | null;
  usage_summary: SubagentRunUsageSummary | null;
  event_counts: SubagentRunEventCounts;
  operator_events: SubagentRunOperatorEvent[];
  output_status: string | null;
  timeout_kind: string | null;
  infrastructure_failure_reason: string | null;
  infrastructure_warning_reason: string | null;
  exit_code: number | null;
  signal: string | null;
  pid: number | null;
  stderr_preview: string | null;
  fallback_model: string | null;
  fallback_from_model: string | null;
  fallback_from_exit_code: number | null;
  heartbeat_count: number;
  assistant_output_count: number;
  last_heartbeat_at: string | null;
  last_assistant_output_at: string | null;
  duration_ms: number | null;
  artifact_dir: string | null;
  event_count: number;
}

export interface SubagentRunUsageSummary {
  input_tokens: number | null;
  output_tokens: number | null;
  cache_read_tokens: number | null;
  cache_write_tokens: number | null;
  total_tokens: number | null;
  total_cost: number | null;
  currency: string | null;
  source: string | null;
  message_count: number | null;
  latest_usage_at: string | null;
}

export interface SubagentRunEventCounts {
  total: number;
  lifecycle: number;
  raw_work: number;
  operator_summary: number;
  debug: number;
}

export interface SubagentRunOperatorEvent {
  event_name: string;
  source: string;
  source_event_type: string | null;
  stream_entry_id: number | null;
  source_message_type: string | null;
  occurred_at: string | null;
  visibility: string;
}

export interface SubagentRunDetail {
  summary: SubagentRunSummary;
  events: AgentStreamEntry[];
  work_events: SubagentRunWorkEvent[];
  artifacts: SubagentRunArtifactSnapshot | null;
}

export interface SubagentRunWorkEvent {
  type: string;
  ts?: number | null;
  source_type?: string | null;
  run_id?: string | null;
  task_id?: number | null;
  subagent_role?: string | null;
  backend?: string | null;
  requested_model?: string | null;
  role?: string | null;
  provider?: string | null;
  model?: string | null;
  update_kind?: string | null;
  content_types?: string[] | null;
  text_preview?: string | null;
  text_chars?: number | null;
  reasoning_kind?: string | null;
  reasoning_chars?: number | null;
  reasoning_redacted?: boolean | null;
  reasoning_summary_preview?: string | null;
  reasoning_summary_chars?: number | null;
  reasoning_summary_source?: string | null;
  thinking_chars?: number | null;
  stop_reason?: string | null;
  tool_call_id?: string | null;
  tool_name?: string | null;
  args_preview?: string | null;
  result_preview?: string | null;
  is_error?: boolean | null;
  tool_calls?: Array<{
    id?: string | null;
    name?: string | null;
    args_preview?: string | null;
  }> | null;
  [key: string]: unknown;
}

export interface AttentionItem {
  id: string;
  project_id: string;
  task_id: number | null;
  run_id: string | null;
  review_round_id: number | null;
  dispatch_id: number | null;
  message_id: number | null;
  kind: string;
  severity: string;
  title: string;
  summary: string;
  created_at: string;
  latest_at: string;
  suggested_action: string;
}

export interface SubagentRunArtifactSnapshot {
  dir: string;
  readable: boolean;
  status_json: string | null;
  events_tail: string | null;
  stdout_tail: string | null;
  stderr_tail: string | null;
  session_file_path: string | null;
  session_tail: string | null;
  read_error: string | null;
}

export interface DispatchEntry {
  id: number;
  project_id: string;
  target_agent: string;
  status: DispatchStatus;
  trigger_type: DispatchTriggerType;
  trigger_id: number;
  task_id: number | null;
  summary: string | null;
  context_prompt: string | null;
  dedup_key: string;
  created_at: string;
  expires_at: string;
  decided_at: string | null;
  completed_at: string | null;
  decided_by: string | null;
  completed_by: string | null;
}

// =========================================================================
// Agents Overview types (mapped from #1694 API)
// =========================================================================

export interface AgentsOverviewRequest {
  projectId?: string;
  channelId?: string;
  scope?: string;
  agentIdentity?: string;
  activityLimit?: number;
  includeLeft?: boolean;
  includeGateway?: boolean;
}

export interface AgentsOverviewResponse {
  agents: AgentOverviewItem[];
  totalCount: number;
  sourceHealth: SourceHealthDto;
}

export interface SourceHealthDto {
  channels: SourceServiceStatusDto | null;
  gateway: SourceServiceStatusDto | null;
}

export interface SourceServiceStatusDto {
  status: string;
  warning?: string | null;
}

export interface AgentOverviewItem {
  agentIdentity: string;
  operatorStatus: string | null;
  workState: string | null;
  severity: string | null;
  summary: AgentSummaryDto | null;
  flags: string[];
  links: AgentLinksDto | null;
  memberships: ChannelMembershipOverviewDto[] | null;
  bindings: GatewayBindingOverviewDto[] | null;
  deliverySummaries: DeliveryOverviewDto[] | null;
  recentActivity: ActivityEventOverviewDto[] | null;
}

export interface AgentSummaryDto {
  channelCount: number;
  activeMembershipCount: number;
  activeDeliveryCount: number;
  recentActivityCount: number;
  latestActivityAt: string | null;
  highestSeverity: string | null;
}

export interface AgentLinksDto {
  self: string | null;
  memberships: string | null;
  bindings: string | null;
  activity: string | null;
}

export interface ChannelMembershipOverviewDto {
  channelId: number;
  channelSlug: string;
  channelDisplayName: string;
  channelKind: string;
  projectId: string | null;
  membershipStatus: string;
  wakePolicy: string;
  canSend: boolean;
  settingsLabel: string | null;
}

export interface GatewayBindingOverviewDto {
  agentKey: string | null;
  role: string | null;
  bindingFreshness: string | null;
  deliveryState: string | null;
  deliveryCounts: GatewayDeliveryCountsDto | null;
  adapterInstances: GatewayAdapterInstanceDto[] | null;
}

export interface GatewayDeliveryCountsDto {
  active: number;
  completed: number;
  failed: number;
  suppressed: number;
  total: number;
}

export interface GatewayAdapterInstanceDto {
  adapterKind: string | null;
  adapterInstanceId: string | null;
  status: string;
  lastSeenAt: string | null;
  expiresAt: string | null;
  isStale: boolean;
  stalenessReason: string | null;
  metadata: Record<string, string> | null;
}

export interface DeliveryOverviewDto {
  deliveryRequestId: string | null;
  state: string | null;
  status: string | null;
  terminal: boolean;
  createdAt: string | null;
  updatedAt: string | null;
  summary: string | null;
}

export interface ActivityEventOverviewDto {
  id: number;
  channelId: number;
  projectId: string | null;
  agentIdentity: string;
  deliveryRequestId: string | null;
  hermesSessionKey: string | null;
  displayBlockId: string | null;
  workerRunId: string | null;
  workerRole: string | null;
  taskId: number | null;
  eventType: string;
  status: string;
  deliveryStage: string;
  terminal: boolean;
  title: string | null;
  summary: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface AgentDetailResponse {
  agentIdentity: string;
  memberships: ChannelMembershipOverviewDto[] | null;
  bindings: GatewayBindingOverviewDto[] | null;
  currentDeliveries: DeliveryOverviewDto[] | null;
  recentDeliveries: DeliveryOverviewDto[] | null;
  activityEvents: ActivityEventOverviewDto[] | null;
  taskAssociations: TaskAssociationDto[] | null;
  summary: AgentSummaryDto | null;
  flags: string[];
  sourceHealth: SourceHealthDto;
}

export interface TaskAssociationDto {
  taskId: number | null;
  projectId: string | null;
  title: string | null;
  status: string | null;
  activityCount: number;
  latestActivityAt: string | null;
}

// =========================================================================
// Document Discussion types (#1680)
// =========================================================================

export interface DiscussionComment {
  id: number;
  thread_id: number;
  parent_comment_id: number | null;
  author_identity: string;
  body_markdown: string;
  comment_kind: string;
  status: string;
  created_at: string;
  edited_at: string | null;
}

export interface DocumentDiscussion {
  thread: {
    id: number;
    title: string;
    status: string;
  } | null;
  comments: DiscussionComment[];
}
