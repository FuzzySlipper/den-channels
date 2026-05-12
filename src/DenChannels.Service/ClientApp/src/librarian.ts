import type { LibrarianResponse, RelevantItem } from './api/types';

export type LibrarianItemGroup = {
  key: 'document' | 'task' | 'message' | 'other';
  label: string;
  items: RelevantItem[];
};

export type LibrarianDocumentRef = {
  projectId: string;
  slug: string;
  ref: string;
};

export type LibrarianTaskRef = {
  projectId: string | null;
  taskId: number;
  ref: string;
};

export type LibrarianMessageRef = {
  projectId: string | null;
  messageId: number;
  threadId: number | null;
  ref: string;
};

const GROUPS: Array<{ key: LibrarianItemGroup['key']; label: string }> = [
  { key: 'document', label: 'Documents' },
  { key: 'task', label: 'Tasks' },
  { key: 'message', label: 'Messages' },
  { key: 'other', label: 'Other' },
];

export function groupLibrarianItems(items: RelevantItem[]): LibrarianItemGroup[] {
  return GROUPS
    .map(group => ({
      ...group,
      items: items.filter(item => itemGroupKey(item) === group.key),
    }))
    .filter(group => group.items.length > 0);
}

export function librarianHasResults(response: LibrarianResponse | null): boolean {
  return Boolean(response && (response.relevant_items.length > 0 || response.recommendations.length > 0));
}

export function stableLibrarianRef(item: RelevantItem): string {
  const doc = documentRefFromLibrarianItem(item);
  if (doc) return doc.ref;
  const task = taskRefFromLibrarianItem(item);
  if (task) return task.ref;
  const message = messageRefFromLibrarianItem(item);
  if (message) return message.ref;
  return item.source_id;
}

export function documentRefFromLibrarianItem(item: RelevantItem): LibrarianDocumentRef | null {
  if (item.type.toLowerCase() !== 'document') return null;
  const source = stripDocWrapper(item.source_id.trim());
  const slash = source.indexOf('/');
  if (slash <= 0 || slash === source.length - 1) return null;
  const projectId = source.slice(0, slash).trim();
  const slug = source.slice(slash + 1).trim();
  if (!projectId || !slug) return null;
  return { projectId, slug, ref: `[doc: ${projectId}/${slug}]` };
}

export function taskRefFromLibrarianItem(item: RelevantItem): LibrarianTaskRef | null {
  if (item.type.toLowerCase() !== 'task') return null;
  const match = item.source_id.match(/#?(\d+)/);
  if (!match) return null;
  return {
    projectId: item.project_id ?? null,
    taskId: Number(match[1]),
    ref: `#${Number(match[1])}`,
  };
}

export function messageRefFromLibrarianItem(item: RelevantItem): LibrarianMessageRef | null {
  if (item.type.toLowerCase() !== 'message') return null;
  const messageMatch = item.source_id.match(/msg#?(\d+)/i) ?? item.source_id.match(/#(\d+)/);
  if (!messageMatch) return null;
  const threadMatch = item.source_id.match(/thread#?(\d+)/i);
  const messageId = Number(messageMatch[1]);
  return {
    projectId: item.project_id ?? null,
    messageId,
    threadId: threadMatch ? Number(threadMatch[1]) : null,
    ref: threadMatch ? `msg#${messageId} thread#${Number(threadMatch[1])}` : `msg#${messageId}`,
  };
}

function itemGroupKey(item: RelevantItem): LibrarianItemGroup['key'] {
  const type = item.type.toLowerCase();
  if (type === 'document' || type === 'task' || type === 'message') return type;
  return 'other';
}

function stripDocWrapper(sourceId: string): string {
  const match = sourceId.match(/^\[?doc:\s*([^\]]+)\]?$/i);
  return match ? match[1].trim() : sourceId;
}
