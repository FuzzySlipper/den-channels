import { useState, useCallback, useMemo } from 'react';
import type { AgentStreamEntry, DispatchEntry, Document, DocumentSummary, Message, MessageIntent, Space, SubagentRunSummary } from './api/types';
import {
  getDispatch,
  listProjects,
  listSpaces,
  listTasks,
  getMessage,
  getMessageFeed,
  getThread,
  listAgentStream,
  listSubagentRuns,
  subagentRunEventsUrl,
  getSubagentRun,
  listDocuments,
  listActiveAgents,
} from './api/client';
import { usePolling } from './hooks/usePolling';
import { useEventSourceRefresh } from './hooks/useEventSourceRefresh';
import { ProjectSidebar } from './components/ProjectSidebar';
import { TaskTree } from './components/TaskTree';
import { TaskDetail } from './components/TaskDetail';
import { FilterBar } from './components/FilterBar';
import { MessageFeed } from './components/MessageFeed';
import { MessageDetail } from './components/MessageDetail';
import { AgentStreamFeed } from './components/AgentStreamFeed';
import { AgentStreamDetail } from './components/AgentStreamDetail';
import { ThoughtFeed } from './components/ThoughtFeed';
import { SubagentRunPanel } from './components/SubagentRunPanel';
import { SubagentRunDetail } from './components/SubagentRunDetail';
import { DocumentList } from './components/DocumentList';
import { DocumentDetail } from './components/DocumentDetail';
import { LibrarianView } from './components/LibrarianView';
import { GitView } from './components/GitView';
import { AgentBar } from './components/AgentBar';
import { DispatchDetail } from './components/DispatchDetail';
import { MESSAGE_INTENT_OPTIONS, messageIntentLabel } from './messageIntents';
import { agentStreamEntryVisibility } from './subagentRuns';
import type { SubagentRunFilter } from './subagentRuns';
import { documentSelectionAction } from './documentEditor';
import type { GitFocus } from './git';
import {
  filterThoughtItems,
  hasRawReasoningPreview,
  sortThoughtItems,
  thoughtItemFromStreamEntry,
  thoughtItemsFromSubagentRunDetail,
} from './thoughts';

const GLOBAL_SPACE: Space = {
  id: '_global',
  name: 'Global',
  kind: 'system',
  visibility: 'normal',
  owner: null,
  root_path: null,
  description: 'Cross-space documents and feeds',
  created_at: null,
  updated_at: null,
};

function withGlobalSpace(spaces: Space[] | null | undefined): Space[] {
  const list = spaces ?? [];
  return list.some(space => space.id === GLOBAL_SPACE.id) ? list : [GLOBAL_SPACE, ...list];
}

function defaultSpaceId(spaces: Space[]): string | null {
  return spaces.find(space => space.kind === 'project' && space.visibility === 'normal')?.id
    ?? spaces.find(space => space.id !== GLOBAL_SPACE.id)?.id
    ?? spaces[0]?.id
    ?? null;
}

function spaceSupportsGit(space: Space | null | undefined, isGlobal: boolean): boolean {
  return isGlobal || space?.kind === 'project' || Boolean(space?.root_path?.trim());
}

export default function App() {
  const [selectedSpaceId, setSelectedSpaceId] = useState<string | null>(null);
  const [selectedTaskId, setSelectedTaskId] = useState<number | null>(null);
  const [selectedTaskProjectId, setSelectedTaskProjectId] = useState<string | null>(null);
  const [selectedMessage, setSelectedMessage] = useState<Message | null>(null);
  const [selectedStreamEntry, setSelectedStreamEntry] = useState<AgentStreamEntry | null>(null);
  const [selectedSubagentRun, setSelectedSubagentRun] = useState<SubagentRunSummary | null>(null);
  const [selectedDispatch, setSelectedDispatch] = useState<DispatchEntry | null>(null);
  const [selectedDoc, setSelectedDoc] = useState<DocumentSummary | null>(null);
  const [documentDetailDirty, setDocumentDetailDirty] = useState(false);
  const [pendingDocumentSwitch, setPendingDocumentSwitch] = useState<DocumentSummary | null>(null);
  const [gitFocus, setGitFocus] = useState<GitFocus | null>(null);
  const [viewMode, setViewMode] = useState<'tasks' | 'documents' | 'librarian' | 'git'>('tasks');
  const [feedMode, setFeedMode] = useState<'stream' | 'messages' | 'thoughts'>('stream');
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [messageIntentFilter, setMessageIntentFilter] = useState<MessageIntent | ''>('');
  const [streamKindFilter, setStreamKindFilter] = useState<'ops' | 'message'>('ops');
  const [streamEventFilter, setStreamEventFilter] = useState('');
  const [streamProjectFilter, setStreamProjectFilter] = useState('');
  const [streamSenderFilter, setStreamSenderFilter] = useState('');
  const [streamRecipientFilter, setStreamRecipientFilter] = useState('');
  const [streamTaskFilter, setStreamTaskFilter] = useState('');
  const [showRawSubagentWorkEvents, setShowRawSubagentWorkEvents] = useState(false);
  const [thoughtProjectFilter, setThoughtProjectFilter] = useState('');
  const [thoughtTaskFilter, setThoughtTaskFilter] = useState('');
  const [thoughtAgentFilter, setThoughtAgentFilter] = useState('');
  const [thoughtRoleFilter, setThoughtRoleFilter] = useState('');
  const [thoughtRawMode, setThoughtRawMode] = useState(false);
  const [subagentRunFilter, setSubagentRunFilter] = useState<SubagentRunFilter>('all');
  const [subagentRunLimit, setSubagentRunLimit] = useState(8);
  const [sortMode, setSortMode] = useState('priority');

  const fetchProjects = useCallback(() => listProjects(), []);
  const { data: projects } = usePolling(fetchProjects, 5000);
  const fetchSpaces = useCallback(() => listSpaces({ includeHidden: true, includeArchived: true }), []);
  const { data: fetchedSpaces } = usePolling(fetchSpaces, 5000);
  const spaces = useMemo(() => withGlobalSpace(fetchedSpaces), [fetchedSpaces]);

  // Auto-select a normal project-kind space when possible to preserve existing project-centric startup.
  const effectiveSpaceId = selectedSpaceId ?? defaultSpaceId(spaces);
  const activeSpace = spaces.find(space => space.id === effectiveSpaceId) ?? null;
  const isGlobal = effectiveSpaceId === '_global';
  const activeSpaceSupportsGit = spaceSupportsGit(activeSpace, isGlobal);

  const fetchTasks = useCallback(
    () => effectiveSpaceId
      ? listTasks(effectiveSpaceId, { tree: true, status: statusFilter ?? undefined })
      : Promise.resolve([]),
    [effectiveSpaceId, statusFilter],
  );
  const { data: tasks } = usePolling(fetchTasks, 5000);

  const fetchMessages = useCallback(
    () => effectiveSpaceId
      ? getMessageFeed(effectiveSpaceId, {
        limit: 15,
        intent: messageIntentFilter || undefined,
      })
      : Promise.resolve([]),
    [effectiveSpaceId, messageIntentFilter],
  );
  const { data: messages } = usePolling(fetchMessages, 5000);

  const parsedStreamTaskId = useMemo(() => {
    const trimmed = streamTaskFilter.trim();
    return /^\d+$/.test(trimmed) ? Number(trimmed) : undefined;
  }, [streamTaskFilter]);

  const parsedThoughtTaskId = useMemo(() => {
    const trimmed = thoughtTaskFilter.trim();
    return /^\d+$/.test(trimmed) ? Number(trimmed) : undefined;
  }, [thoughtTaskFilter]);

  const fetchAgentStream = useCallback(
    () => effectiveSpaceId
      ? listAgentStream({
        projectId: isGlobal ? (streamProjectFilter.trim() || undefined) : effectiveSpaceId,
        taskId: parsedStreamTaskId,
        streamKind: streamKindFilter,
        eventType: streamEventFilter || undefined,
        sender: streamSenderFilter.trim() || undefined,
        limit: 60,
      })
      : Promise.resolve([]),
    [
      effectiveSpaceId,
      isGlobal,
      parsedStreamTaskId,
      streamEventFilter,
      streamKindFilter,
      streamProjectFilter,
      streamSenderFilter,
    ],
  );
  const { data: agentStream } = usePolling(fetchAgentStream, 5000);

  const fetchSubagentRuns = useCallback(
    () => effectiveSpaceId
      ? listSubagentRuns({
        projectId: isGlobal ? (streamProjectFilter.trim() || undefined) : effectiveSpaceId,
        taskId: parsedStreamTaskId,
        state: subagentRunFilter === 'all' ? undefined : subagentRunFilter,
        limit: subagentRunLimit,
      })
      : Promise.resolve([]),
    [effectiveSpaceId, isGlobal, parsedStreamTaskId, streamProjectFilter, subagentRunFilter, subagentRunLimit],
  );
  const {
    data: subagentRuns,
    loading: subagentRunsLoading,
    error: subagentRunsError,
    refresh: refreshSubagentRuns,
  } = usePolling(fetchSubagentRuns, 2000);
  const subagentRunEvents = useMemo(
    () => effectiveSpaceId
      ? subagentRunEventsUrl({
        projectId: isGlobal ? (streamProjectFilter.trim() || undefined) : effectiveSpaceId,
        taskId: parsedStreamTaskId,
      })
      : null,
    [effectiveSpaceId, isGlobal, parsedStreamTaskId, streamProjectFilter],
  );
  useEventSourceRefresh(subagentRunEvents, 'subagent_run_updated', refreshSubagentRuns);

  const fetchThoughts = useCallback(async () => {
    if (!effectiveSpaceId || feedMode !== 'thoughts') return [];

    const projectFilter = isGlobal ? (thoughtProjectFilter.trim() || undefined) : effectiveSpaceId;
    const [streamEntries, runs] = await Promise.all([
      listAgentStream({
        projectId: projectFilter,
        taskId: parsedThoughtTaskId,
        streamKind: 'ops',
        sender: thoughtAgentFilter.trim() || undefined,
        limit: 100,
      }),
      listSubagentRuns({
        projectId: projectFilter,
        taskId: parsedThoughtTaskId,
        limit: 10,
      }),
    ]);

    const runDetails = await Promise.allSettled(runs.slice(0, 8).map(run => getSubagentRun(run.run_id, {
      projectId: run.project_id ?? projectFilter,
      taskId: run.task_id ?? undefined,
    })));

    const streamThoughts = streamEntries
      .map(thoughtItemFromStreamEntry)
      .filter(item => item !== null);
    const runThoughts = runDetails.flatMap(result => result.status === 'fulfilled'
      ? thoughtItemsFromSubagentRunDetail(result.value)
      : []);

    return filterThoughtItems(sortThoughtItems([...streamThoughts, ...runThoughts]), {
      project: isGlobal ? thoughtProjectFilter : undefined,
      taskId: parsedThoughtTaskId,
      agent: thoughtAgentFilter,
      role: thoughtRoleFilter,
    });
  }, [
    effectiveSpaceId,
    feedMode,
    isGlobal,
    parsedThoughtTaskId,
    thoughtAgentFilter,
    thoughtProjectFilter,
    thoughtRoleFilter,
  ]);
  const {
    data: thoughts,
    loading: thoughtsLoading,
    error: thoughtsError,
    refresh: refreshThoughts,
  } = usePolling(fetchThoughts, 4000);
  const thoughtSubagentRunEvents = useMemo(
    () => effectiveSpaceId
      ? subagentRunEventsUrl({
        projectId: isGlobal ? (thoughtProjectFilter.trim() || undefined) : effectiveSpaceId,
        taskId: parsedThoughtTaskId,
      })
      : null,
    [effectiveSpaceId, isGlobal, parsedThoughtTaskId, thoughtProjectFilter],
  );
  useEventSourceRefresh(thoughtSubagentRunEvents, 'subagent_run_updated', refreshThoughts);

  const fetchDocs = useCallback(
    () => effectiveSpaceId
      ? listDocuments(isGlobal ? undefined : effectiveSpaceId)
      : Promise.resolve([]),
    [effectiveSpaceId, isGlobal],
  );
  const { data: documents, refresh: refreshDocs } = usePolling(fetchDocs, 5000);

  const fetchAgents = useCallback(
    () => listActiveAgents(isGlobal ? undefined : (effectiveSpaceId ?? undefined)),
    [effectiveSpaceId, isGlobal],
  );
  const { data: agents } = usePolling(fetchAgents, 5000);

  const sortedDocs = useMemo(
    () => documents ? [...documents].sort((a, b) => b.updated_at.localeCompare(a.updated_at)) : [],
    [documents],
  );
  const filteredAgentStream = useMemo(() => {
    const recipientFilter = streamRecipientFilter.trim().toLowerCase();
    return (agentStream ?? []).filter(entry => {
      if (!showRawSubagentWorkEvents && !streamEventFilter && agentStreamEntryVisibility(entry) === 'debug') {
        return false;
      }
      if (!recipientFilter) {
        return true;
      }
      const recipients = [
        entry.recipient_agent,
        entry.recipient_role,
        entry.recipient_instance_id,
      ]
        .filter((value): value is string => Boolean(value))
        .map(value => value.toLowerCase());
      return recipients.some(value => value.includes(recipientFilter));
    });
  }, [agentStream, showRawSubagentWorkEvents, streamEventFilter, streamRecipientFilter]);
  const streamEventOptions = useMemo(() => {
    const options = new Set((agentStream ?? []).map(entry => entry.event_type));
    if (streamEventFilter) {
      options.add(streamEventFilter);
    }
    return Array.from(options).sort((left, right) => left.localeCompare(right));
  }, [agentStream, streamEventFilter]);
  const rawReasoningAvailable = useMemo(() => hasRawReasoningPreview(thoughts ?? []), [thoughts]);

  const taskCount = tasks?.length ?? 0;
  const filterLabel = statusFilter ? ` [${statusFilter}]` : '';
  const messageFilterLabel = messageIntentFilter ? ` [${messageIntentFilter}]` : '';
  const sortLabel = sortMode !== 'priority' ? ` \u2195${sortMode}` : '';

  const handleProjectSelect = useCallback((id: string) => {
    setSelectedSpaceId(id);
    setSelectedTaskId(null);
    setSelectedTaskProjectId(null);
    setSelectedMessage(null);
    setSelectedStreamEntry(null);
    setSelectedSubagentRun(null);
    setSelectedDispatch(null);
    setSelectedDoc(null);
  }, []);

  const handleTaskSelect = useCallback((taskId: number, projectId?: string | null) => {
    const targetProjectId = projectId?.trim() || effectiveSpaceId;
    if (targetProjectId && targetProjectId !== selectedSpaceId) {
      setSelectedSpaceId(targetProjectId);
    }
    setSelectedTaskId(taskId);
    setSelectedTaskProjectId(targetProjectId ?? null);
    setSelectedMessage(null);
    setSelectedStreamEntry(null);
    setSelectedSubagentRun(null);
    setSelectedDispatch(null);
    setSelectedDoc(null);
    setViewMode('tasks');
  }, [effectiveSpaceId, selectedSpaceId]);

  const handleMessageSelect = useCallback((message: Message) => {
    setSelectedMessage(message);
    setSelectedStreamEntry(null);
    setSelectedSubagentRun(null);
    setSelectedDispatch(null);
    setSelectedDoc(null);
  }, []);

  const handleStreamSelect = useCallback((entry: AgentStreamEntry) => {
    setSelectedStreamEntry(entry);
    setSelectedSubagentRun(null);
    setSelectedDispatch(null);
    setSelectedDoc(null);
  }, []);

  const handleSubagentRunSelect = useCallback((run: SubagentRunSummary) => {
    setSelectedSubagentRun(run);
    setSelectedStreamEntry(null);
    setSelectedDispatch(null);
    setSelectedDoc(null);
  }, []);

  const handleDispatchSelect = useCallback(async (dispatchId: number) => {
    try {
      const dispatch = await getDispatch(dispatchId);
      setSelectedDispatch(dispatch);
      setSelectedStreamEntry(null);
      setSelectedSubagentRun(null);
      setSelectedDoc(null);
    } catch (error) {
      console.error('Failed to load dispatch detail', error);
    }
  }, []);

  const applyDocumentSelection = useCallback((doc: DocumentSummary) => {
    if (doc.project_id && doc.project_id !== selectedSpaceId) {
      setSelectedSpaceId(doc.project_id);
    }
    setSelectedDoc(doc);
    setDocumentDetailDirty(false);
    setPendingDocumentSwitch(null);
    setSelectedTaskId(null);
    setSelectedTaskProjectId(null);
    setSelectedMessage(null);
    setSelectedStreamEntry(null);
    setSelectedSubagentRun(null);
    setSelectedDispatch(null);
  }, [selectedSpaceId]);

  const handleDocumentSelect = useCallback((doc: DocumentSummary) => {
    const action = documentSelectionAction(selectedDoc, doc, documentDetailDirty);
    if (action === 'keep_current') {
      setPendingDocumentSwitch(null);
      return;
    }
    if (action === 'prompt_for_dirty_switch') {
      setPendingDocumentSwitch(doc);
      return;
    }
    applyDocumentSelection(doc);
  }, [applyDocumentSelection, documentDetailDirty, selectedDoc]);

  const handleDocumentSaved = useCallback((doc: Document) => {
    setSelectedDoc({
      id: doc.id,
      project_id: doc.project_id,
      slug: doc.slug,
      title: doc.title,
      doc_type: doc.doc_type,
      tags: doc.tags,
      updated_at: doc.updated_at,
    });
    refreshDocs();
  }, [refreshDocs]);

  const handleDocumentDirtyChange = useCallback((dirty: boolean) => {
    setDocumentDetailDirty(dirty);
    if (!dirty) {
      setPendingDocumentSwitch(null);
    }
  }, []);

  const handleMessageOpen = useCallback(async (projectId: string, messageId: number) => {
    try {
      const message = await getMessage(projectId, messageId);
      if (!message) return;
      setSelectedMessage(message);
      setSelectedStreamEntry(null);
      setSelectedSubagentRun(null);
      setSelectedDispatch(null);
      setSelectedDoc(null);
    } catch (error) {
      console.error('Failed to load message detail', error);
    }
  }, []);

  const handleThreadOpen = useCallback(async (projectId: string, threadId: number) => {
    try {
      const thread = await getThread(projectId, threadId);
      setSelectedMessage(thread.root);
      setSelectedStreamEntry(null);
      setSelectedSubagentRun(null);
      setSelectedDispatch(null);
      setSelectedDoc(null);
    } catch (error) {
      console.error('Failed to load thread detail', error);
    }
  }, []);

  const handleOpenGitFocus = useCallback((focus: GitFocus) => {
    if (focus.projectId !== selectedSpaceId) {
      setSelectedSpaceId(focus.projectId);
    }
    setGitFocus(focus);
    setViewMode('git');
    setSelectedTaskId(null);
    setSelectedTaskProjectId(null);
    setSelectedMessage(null);
    setSelectedStreamEntry(null);
    setSelectedSubagentRun(null);
    setSelectedDispatch(null);
    setSelectedDoc(null);
  }, [selectedSpaceId]);

  const handleStreamThreadOpen = useCallback(async (entry: AgentStreamEntry) => {
    if (!entry.project_id || entry.thread_id == null) {
      return;
    }

    await handleThreadOpen(entry.project_id, entry.thread_id);
  }, [handleThreadOpen]);

  const feedCount = feedMode === 'stream'
    ? filteredAgentStream.length
    : feedMode === 'thoughts'
      ? (thoughts?.length ?? 0)
      : (messages?.length ?? 0);
  const feedTitle = feedMode === 'stream'
    ? 'Agent Stream'
    : feedMode === 'thoughts'
      ? 'Thoughts'
      : 'Messages';

  return (
    <div className="dashboard">
      {/* Feed — top, full width */}
      <div className="panel panel-messages">
        <div className="panel-header">
          {feedTitle}
          {effectiveSpaceId && (
            <span className="count">
              ({feedCount}{feedMode === 'messages' ? messageFilterLabel : ''})
            </span>
          )}
        </div>
        <div className="feed-toolbar">
          <div className="feed-toggle">
            <button
              className={feedMode === 'stream' ? 'active' : ''}
              onClick={() => setFeedMode('stream')}
            >
              Stream
            </button>
            <button
              className={feedMode === 'messages' ? 'active' : ''}
              onClick={() => setFeedMode('messages')}
            >
              Messages
            </button>
            <button
              className={feedMode === 'thoughts' ? 'active' : ''}
              onClick={() => setFeedMode('thoughts')}
            >
              Thoughts
            </button>
          </div>

          {feedMode === 'stream' ? (
            <>
              <label className="panel-filter-label" htmlFor="stream-kind-filter">Kind</label>
              <select
                id="stream-kind-filter"
                className="panel-filter-select"
                value={streamKindFilter}
                onChange={e => setStreamKindFilter(e.target.value as 'ops' | 'message')}
              >
                <option value="ops">Ops</option>
                <option value="message">Messages</option>
              </select>

              <label className="panel-filter-label" htmlFor="stream-event-filter">Event</label>
              <select
                id="stream-event-filter"
                className="panel-filter-select"
                value={streamEventFilter}
                onChange={e => setStreamEventFilter(e.target.value)}
              >
                <option value="">All</option>
                {streamEventOptions.map(eventType => (
                  <option key={eventType} value={eventType}>{eventType}</option>
                ))}
              </select>

              {isGlobal && (
                <input
                  className="feed-text-filter"
                  value={streamProjectFilter}
                  onChange={e => setStreamProjectFilter(e.target.value)}
                  placeholder="Space"
                />
              )}

              <input
                className="feed-text-filter"
                value={streamSenderFilter}
                onChange={e => setStreamSenderFilter(e.target.value)}
                placeholder="Sender"
              />

              <input
                className="feed-text-filter"
                value={streamRecipientFilter}
                onChange={e => setStreamRecipientFilter(e.target.value)}
                placeholder="Recipient"
              />

              <input
                className="feed-text-filter feed-text-filter-short"
                value={streamTaskFilter}
                onChange={e => setStreamTaskFilter(e.target.value)}
                placeholder="Task #"
              />

              <label
                className="thought-raw-toggle"
                title="Show verbose subagent_work_* audit events in the normal stream feed"
              >
                <input
                  type="checkbox"
                  checked={showRawSubagentWorkEvents}
                  onChange={e => setShowRawSubagentWorkEvents(e.target.checked)}
                />
                Raw sub-agent work
              </label>
            </>
          ) : feedMode === 'messages' ? (
            <>
              <label className="panel-filter-label" htmlFor="message-intent-filter">Intent</label>
              <select
                id="message-intent-filter"
                className="panel-filter-select"
                value={messageIntentFilter}
                onChange={e => setMessageIntentFilter((e.target.value as MessageIntent) || '')}
                title={messageIntentFilter ? `Filtering messages by ${messageIntentLabel(messageIntentFilter)}` : 'Show all message intents'}
              >
                <option value="">All</option>
                {MESSAGE_INTENT_OPTIONS.map(option => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
            </>
          ) : (
            <>
              {isGlobal && (
                <input
                  className="feed-text-filter"
                  value={thoughtProjectFilter}
                  onChange={e => setThoughtProjectFilter(e.target.value)}
                  placeholder="Space"
                />
              )}

              <input
                className="feed-text-filter"
                value={thoughtAgentFilter}
                onChange={e => setThoughtAgentFilter(e.target.value)}
                placeholder="Agent"
              />

              <input
                className="feed-text-filter"
                value={thoughtRoleFilter}
                onChange={e => setThoughtRoleFilter(e.target.value)}
                placeholder="Role"
              />

              <input
                className="feed-text-filter feed-text-filter-short"
                value={thoughtTaskFilter}
                onChange={e => setThoughtTaskFilter(e.target.value)}
                placeholder="Task #"
              />

              <label
                className={`thought-raw-toggle${rawReasoningAvailable ? '' : ' thought-raw-toggle-disabled'}`}
                title={rawReasoningAvailable ? 'Show bounded local raw reasoning previews' : 'No local raw reasoning previews in this feed'}
              >
                <input
                  type="checkbox"
                  checked={thoughtRawMode && rawReasoningAvailable}
                  disabled={!rawReasoningAvailable}
                  onChange={e => setThoughtRawMode(e.target.checked)}
                />
                Raw local
              </label>
            </>
          )}
        </div>
        <div className="panel-body">
          {feedMode === 'stream' ? (
            <AgentStreamFeed
              entries={filteredAgentStream}
              isGlobal={isGlobal}
              onSelect={handleStreamSelect}
              onOpenTask={handleTaskSelect}
              onOpenThread={entry => void handleStreamThreadOpen(entry)}
              onOpenDispatch={dispatchId => void handleDispatchSelect(dispatchId)}
            />
          ) : feedMode === 'messages' ? (
            <MessageFeed
              messages={messages ?? []}
              isGlobal={isGlobal}
              onSelect={handleMessageSelect}
            />
          ) : (
            <ThoughtFeed
              items={thoughts ?? []}
              isGlobal={isGlobal}
              loading={thoughtsLoading}
              error={thoughtsError}
              showRawReasoning={thoughtRawMode && rawReasoningAvailable}
              rawReasoningAvailable={rawReasoningAvailable}
              onOpenTask={handleTaskSelect}
              onOpenRun={handleSubagentRunSelect}
              onOpenStream={handleStreamSelect}
            />
          )}
        </div>
      </div>

      {/* Middle row — agents plus active sub-agent work */}
      <div className="panel-middle-grid">
        <div className="panel panel-agents">
          <div className="panel-header">
            Agents <span className="count">({agents?.length ?? 0})</span>
          </div>
          <div className="panel-body panel-body-agents">
            <AgentBar agents={agents ?? []} isGlobal={isGlobal} />
          </div>
        </div>

        <div className="panel panel-subagents">
          <div className="panel-header">
            Sub-agent Runs <span className="count">({subagentRuns?.length ?? 0})</span>
          </div>
          <div className="panel-body">
            <SubagentRunPanel
              runs={subagentRuns ?? []}
              totalCount={subagentRuns?.length ?? 0}
              isGlobal={isGlobal}
              filter={subagentRunFilter}
              limit={subagentRunLimit}
              loading={subagentRunsLoading}
              error={subagentRunsError}
              onFilterChange={setSubagentRunFilter}
              onLimitChange={setSubagentRunLimit}
              onRefresh={refreshSubagentRuns}
              onSelectRun={handleSubagentRunSelect}
              onOpenTask={handleTaskSelect}
            />
          </div>
        </div>
      </div>

      {/* Spaces sidebar — bottom left */}
      <ProjectSidebar
        spaces={spaces}
        selectedId={effectiveSpaceId}
        onSelect={handleProjectSelect}
      />

      {/* Main area — bottom right */}
      <div className="panel panel-main">
        <div className="panel-header">
          {viewMode === 'tasks'
            ? <>Tasks {effectiveSpaceId && <span className="count">({taskCount}{filterLabel}{sortLabel})</span>}</>
            : viewMode === 'documents'
              ? <>Documents {effectiveSpaceId && <span className="count">({sortedDocs.length})</span>}</>
              : viewMode === 'git'
                ? <>Git</>
                : <>Librarian</>
          }
        </div>
        <FilterBar
          statusFilter={statusFilter}
          onStatusFilterChange={setStatusFilter}
          sortMode={sortMode}
          onSortChange={setSortMode}
          viewMode={viewMode}
          onViewModeChange={setViewMode}
        />
        <div className="panel-body">
          {viewMode === 'tasks' ? (
            <TaskTree
              tasks={tasks ?? []}
              selectedTaskId={selectedTaskId}
              onSelect={handleTaskSelect}
              statusFilter={statusFilter}
              sortMode={sortMode}
            />
          ) : viewMode === 'documents' ? (
            <DocumentList
              documents={sortedDocs}
              projectId={effectiveSpaceId}
              isGlobal={isGlobal}
              onSelect={handleDocumentSelect}
            />
          ) : viewMode === 'git' ? (
            <GitView
              projectId={activeSpaceSupportsGit ? effectiveSpaceId : null}
              projects={projects ?? []}
              isGlobal={isGlobal}
              scopeSupportsGit={activeSpaceSupportsGit}
              focus={gitFocus}
              onClearFocus={() => setGitFocus(null)}
            />
          ) : (
            <LibrarianView
              projects={spaces}
              currentProjectId={effectiveSpaceId}
              onOpenTask={handleTaskSelect}
              onOpenDocument={handleDocumentSelect}
              onOpenMessage={(projectId, messageId) => void handleMessageOpen(projectId, messageId)}
              onOpenThread={(projectId, threadId) => void handleThreadOpen(projectId, threadId)}
            />
          )}
        </div>
      </div>

      {/* Detail overlays */}
      {selectedTaskId != null && effectiveSpaceId && (
        <TaskDetail
          key={`${selectedTaskProjectId ?? effectiveSpaceId}:${selectedTaskId}`}
          projectId={selectedTaskProjectId ?? effectiveSpaceId}
          taskId={selectedTaskId}
          onSelectTask={handleTaskSelect}
          onSelectMessage={handleMessageSelect}
          onSelectRun={handleSubagentRunSelect}
          onOpenGit={handleOpenGitFocus}
          onClose={() => {
            setSelectedTaskId(null);
            setSelectedTaskProjectId(null);
          }}
        />
      )}

      {selectedMessage && (
        <MessageDetail
          key={`${selectedMessage.project_id}:${selectedMessage.id}`}
          message={selectedMessage}
          onClose={() => setSelectedMessage(null)}
        />
      )}

      {selectedStreamEntry && (
        <AgentStreamDetail
          key={selectedStreamEntry.id}
          entry={selectedStreamEntry}
          onClose={() => setSelectedStreamEntry(null)}
          onOpenTask={handleTaskSelect}
          onOpenThread={entry => void handleStreamThreadOpen(entry)}
          onOpenDispatch={dispatchId => void handleDispatchSelect(dispatchId)}
        />
      )}

      {selectedSubagentRun && (
        <SubagentRunDetail
          key={selectedSubagentRun.run_id}
          run={selectedSubagentRun}
          onClose={() => setSelectedSubagentRun(null)}
          onOpenTask={handleTaskSelect}
          onOpenEntry={handleStreamSelect}
        />
      )}

      {selectedDispatch && (
        <DispatchDetail
          dispatch={selectedDispatch}
          onClose={() => setSelectedDispatch(null)}
          onOpenTask={handleTaskSelect}
        />
      )}

      {selectedDoc && (
        <DocumentDetail
          summary={selectedDoc}
          onClose={() => {
            setSelectedDoc(null);
            setDocumentDetailDirty(false);
            setPendingDocumentSwitch(null);
          }}
          onSaved={handleDocumentSaved}
          onOpenDocument={handleDocumentSelect}
          onDirtyChange={handleDocumentDirtyChange}
          pendingSwitch={pendingDocumentSwitch}
          onCancelSwitch={() => setPendingDocumentSwitch(null)}
          onConfirmSwitch={applyDocumentSelection}
        />
      )}
    </div>
  );
}
