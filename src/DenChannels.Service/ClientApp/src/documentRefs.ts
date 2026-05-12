import type { DocumentSummary } from './api/types';

export interface DocumentReference {
  projectId: string;
  slug: string;
  ref: string;
}

export interface DocumentReferenceTextPart {
  kind: 'text';
  text: string;
}

export interface DocumentReferenceLinkPart extends DocumentReference {
  kind: 'document_ref';
}

export type DocumentReferencePart = DocumentReferenceTextPart | DocumentReferenceLinkPart;

const DOCUMENT_REF_REGEX = /\[doc:\s*([^\s\]/]+)\/([^\]\s]+)\]/g;

export function parseDocumentReference(value: string): DocumentReference | null {
  const match = /^\[doc:\s*([^\s\]/]+)\/([^\]\s]+)\]$/.exec(value.trim());
  if (!match) return null;

  return {
    projectId: match[1],
    slug: match[2],
    ref: match[0],
  };
}

export function splitDocumentReferenceText(value: string): DocumentReferencePart[] {
  const parts: DocumentReferencePart[] = [];
  let lastIndex = 0;

  for (const match of value.matchAll(DOCUMENT_REF_REGEX)) {
    const index = match.index ?? 0;
    if (index > lastIndex) {
      parts.push({ kind: 'text', text: value.slice(lastIndex, index) });
    }

    parts.push({
      kind: 'document_ref',
      projectId: match[1],
      slug: match[2],
      ref: match[0],
    });
    lastIndex = index + match[0].length;
  }

  if (lastIndex < value.length) {
    parts.push({ kind: 'text', text: value.slice(lastIndex) });
  }

  return parts.length > 0 ? parts : [{ kind: 'text', text: value }];
}

export function documentSummaryFromReference(reference: DocumentReference): DocumentSummary {
  return {
    id: 0,
    project_id: reference.projectId,
    slug: reference.slug,
    title: reference.slug,
    doc_type: 'note',
    tags: null,
    updated_at: '',
  };
}
