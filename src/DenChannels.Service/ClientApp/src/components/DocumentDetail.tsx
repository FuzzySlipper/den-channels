import { useCallback, useEffect, useState } from 'react';
import type { DocumentSummary, Document } from '../api/types';
import { getDocument, saveDocument } from '../api/client';
import { isDocumentEditorSaveShortcut } from '../documentEditor';
import { documentSummaryFromReference, splitDocumentReferenceText } from '../documentRefs';

interface Props {
  summary: DocumentSummary;
  onClose: () => void;
  onSaved?: (doc: Document) => void;
  onOpenDocument?: (doc: DocumentSummary) => void;
  onDirtyChange?: (dirty: boolean) => void;
  pendingSwitch?: DocumentSummary | null;
  onCancelSwitch?: () => void;
  onConfirmSwitch?: (doc: DocumentSummary) => void;
}

export function DocumentDetail({ summary, onClose, onSaved, onOpenDocument, onDirtyChange, pendingSwitch, onCancelSwitch, onConfirmSwitch }: Props) {
  const [doc, setDoc] = useState<Document | null>(null);
  const [draft, setDraft] = useState('');
  const [isEditing, setIsEditing] = useState(false);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  const displayed = doc ?? summary;
  const tags = displayed.tags ?? [];
  const dirty = doc !== null && draft !== doc.content;

  useEffect(() => {
    onDirtyChange?.(dirty);
  }, [dirty, onDirtyChange]);

  useEffect(() => () => onDirtyChange?.(false), [onDirtyChange]);

  useEffect(() => {
    let cancelled = false;

    setDoc(null);
    setDraft('');
    setIsEditing(false);
    setLoading(true);
    setLoadError(null);
    setSaveError(null);
    setSaving(false);

    getDocument(summary.project_id, summary.slug)
      .then(loadedDoc => {
        if (cancelled) return;

        if (loadedDoc) {
          setDoc(loadedDoc);
          setDraft(loadedDoc.content);
        } else {
          setLoadError(`Document '${summary.slug}' was not found in project '${summary.project_id}'.`);
        }
      })
      .catch(error => {
        if (!cancelled) {
          setLoadError(error instanceof Error ? error.message : String(error));
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
        }
      });

    return () => { cancelled = true; };
  }, [summary.project_id, summary.slug]);

  const confirmDiscard = useCallback(() => (
    !dirty || window.confirm('Discard unsaved document changes?')
  ), [dirty]);

  const handleClose = useCallback(() => {
    if (!confirmDiscard()) return;
    onClose();
  }, [confirmDiscard, onClose]);

  const handleStartEdit = useCallback(() => {
    if (!doc) return;
    setDraft(doc.content);
    setIsEditing(true);
    setSaveError(null);
  }, [doc]);

  const handleCancel = useCallback(() => {
    if (!confirmDiscard()) return;

    if (doc) {
      setDraft(doc.content);
    }
    setIsEditing(false);
    setSaveError(null);
  }, [confirmDiscard, doc]);

  const handleSave = useCallback(async () => {
    if (!doc || !dirty || saving) return false;

    setSaving(true);
    setSaveError(null);

    try {
      const saved = await saveDocument(doc.project_id, {
        slug: doc.slug,
        title: doc.title,
        content: draft,
        doc_type: doc.doc_type,
        tags: doc.tags,
      });

      setDoc(saved);
      setDraft(saved.content);
      setIsEditing(false);
      onSaved?.(saved);
      return true;
    } catch (error) {
      setSaveError(error instanceof Error ? error.message : String(error));
      return false;
    } finally {
      setSaving(false);
    }
  }, [doc, dirty, draft, onSaved, saving]);

  const handleSaveAndSwitch = useCallback(async () => {
    if (!pendingSwitch) return;
    const saved = await handleSave();
    if (saved) onConfirmSwitch?.(pendingSwitch);
  }, [handleSave, onConfirmSwitch, pendingSwitch]);

  const handleDiscardAndSwitch = useCallback(() => {
    if (!pendingSwitch) return;
    onConfirmSwitch?.(pendingSwitch);
  }, [onConfirmSwitch, pendingSwitch]);

  useEffect(() => {
    if (!isEditing) return;

    const handleKeyDown = (event: KeyboardEvent) => {
      if (!isDocumentEditorSaveShortcut(event)) return;
      event.preventDefault();
      if (dirty && !saving) {
        void handleSave();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [dirty, handleSave, isEditing, saving]);

  return (
    <div className="detail-overlay document-detail-overlay">
      <div className="detail-header">
        <div className="detail-title-block">
          <h2>{displayed.title}</h2>
          {dirty && <span className="dirty-indicator">Unsaved changes</span>}
        </div>
        <div className="detail-actions">
          {doc && !isEditing && (
            <button className="detail-action" onClick={handleStartEdit}>Edit</button>
          )}
          {isEditing && (
            <>
              <button
                className="detail-action detail-action-primary"
                onClick={() => void handleSave()}
                disabled={!dirty || saving}
              >
                {saving ? 'Saving...' : 'Save'}
              </button>
              <button className="detail-action" onClick={handleCancel} disabled={saving}>Cancel</button>
            </>
          )}
          <button className="detail-close" onClick={handleClose} disabled={saving}>✕</button>
        </div>
      </div>
      <div className="detail-body">
        <div className="detail-section">
          <dl className="detail-meta">
            <dt>Project</dt>
            <dd>{displayed.project_id}</dd>
            <dt>Slug</dt>
            <dd>{displayed.slug}</dd>
            <dt>Title</dt>
            <dd>{displayed.title}</dd>
            <dt>Type</dt>
            <dd>{displayed.doc_type}</dd>
            <dt>Tags</dt>
            <dd>{tags.length > 0 ? tags.join(', ') : '—'}</dd>
            {displayed.updated_at && (
              <><dt>Updated</dt><dd>{new Date(displayed.updated_at + 'Z').toLocaleString()}</dd></>
            )}
          </dl>
        </div>

        {loadError && <div className="detail-error" role="alert">{loadError}</div>}
        {saveError && <div className="detail-error" role="alert">Save failed: {saveError}</div>}
        {pendingSwitch && dirty && (
          <div className="detail-info document-switch-guard" role="status">
            <div>
              Switching to <strong>{pendingSwitch.title}</strong> would discard unsaved edits to this document.
            </div>
            <div className="document-switch-actions">
              <button
                className="detail-action detail-action-primary"
                onClick={() => void handleSaveAndSwitch()}
                disabled={!dirty || saving}
              >
                Save & switch
              </button>
              <button className="detail-action" onClick={handleDiscardAndSwitch} disabled={saving}>Discard & switch</button>
              <button className="detail-action" onClick={onCancelSwitch} disabled={saving}>Keep editing</button>
            </div>
          </div>
        )}
        {isEditing && dirty && (
          <div className="detail-info" role="status">
            You have unsaved changes. Save persists Markdown through the Den document API; Cancel discards the draft. Ctrl+S / Cmd+S also saves while editing.
          </div>
        )}

        <div className="detail-section">
          <div className="detail-section-header">
            <h3>Markdown Content</h3>
            {isEditing && <span className="detail-subtle">{draft.length} characters</span>}
          </div>
          {loading ? (
            <div className="loading">Loading document...</div>
          ) : doc && isEditing ? (
            <textarea
              className="document-editor"
              value={draft}
              onChange={e => setDraft(e.target.value)}
              disabled={saving}
              spellCheck={false}
              aria-label={`Markdown content for ${doc.title}`}
            />
          ) : doc ? (
            <DocumentMarkdownContent content={doc.content} onOpenDocument={onOpenDocument} />
          ) : (
            <div className="empty">No document content available.</div>
          )}
        </div>
      </div>
    </div>
  );
}

interface DocumentMarkdownContentProps {
  content: string;
  onOpenDocument?: (doc: DocumentSummary) => void;
}

function DocumentMarkdownContent({ content, onOpenDocument }: DocumentMarkdownContentProps) {
  if (!content) {
    return <div className="detail-description"><span className="empty-inline">Document is empty.</span></div>;
  }

  const parts = splitDocumentReferenceText(content);

  return (
    <div className="detail-description document-markdown-content">
      {parts.map((part, index) => {
        if (part.kind === 'text') {
          return <span key={`text:${index}`}>{part.text}</span>;
        }

        return (
          <button
            key={`doc-ref:${part.projectId}:${part.slug}:${index}`}
            type="button"
            className="document-ref-link"
            title={`Open ${part.ref}`}
            onClick={() => onOpenDocument?.(documentSummaryFromReference(part))}
          >
            {part.ref}
          </button>
        );
      })}
    </div>
  );
}
