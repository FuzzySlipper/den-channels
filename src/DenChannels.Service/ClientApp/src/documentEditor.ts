import type { DocumentSummary } from './api/types';

export function documentIdentity(doc: DocumentSummary | null | undefined): string | null {
  if (!doc) return null;
  return `${doc.project_id}/${doc.slug}`;
}

export function isSameDocument(a: DocumentSummary | null | undefined, b: DocumentSummary | null | undefined): boolean {
  const aId = documentIdentity(a);
  const bId = documentIdentity(b);
  return aId !== null && aId === bId;
}

export type DocumentSelectionAction = 'keep_current' | 'prompt_for_dirty_switch' | 'select';

export function documentSelectionAction(
  current: DocumentSummary | null | undefined,
  next: DocumentSummary,
  dirty: boolean,
): DocumentSelectionAction {
  if (isSameDocument(current, next)) return 'keep_current';
  if (dirty && current !== null && current !== undefined) return 'prompt_for_dirty_switch';
  return 'select';
}

export function shouldPromptForDirtyDocumentSwitch(
  current: DocumentSummary | null | undefined,
  next: DocumentSummary,
  dirty: boolean,
): boolean {
  return documentSelectionAction(current, next, dirty) === 'prompt_for_dirty_switch';
}

export interface SaveShortcutEventLike {
  key: string;
  ctrlKey?: boolean;
  metaKey?: boolean;
  altKey?: boolean;
  shiftKey?: boolean;
}

export function isDocumentEditorSaveShortcut(event: SaveShortcutEventLike): boolean {
  return (event.ctrlKey === true || event.metaKey === true)
    && event.altKey !== true
    && event.key.toLowerCase() === 's';
}
