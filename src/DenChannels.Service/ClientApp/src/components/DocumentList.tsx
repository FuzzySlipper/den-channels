import { useState, useCallback } from 'react';
import type { DocumentSummary, DocumentSearchResult } from '../api/types';
import { searchDocuments } from '../api/client';
import { formatTimeAgo } from '../utils';

interface Props {
  documents: DocumentSummary[];
  projectId: string | null;
  isGlobal: boolean;
  onSelect: (doc: DocumentSummary) => void;
}

export function DocumentList({ documents, projectId, isGlobal, onSelect }: Props) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<DocumentSearchResult[] | null>(null);
  const [searching, setSearching] = useState(false);

  const handleSearch = useCallback(async () => {
    if (!query.trim()) {
      setResults(null);
      return;
    }
    setSearching(true);
    try {
      const r = await searchDocuments(query, isGlobal ? undefined : (projectId ?? undefined));
      setResults(r);
    } catch {
      setResults(null);
    } finally {
      setSearching(false);
    }
  }, [query, projectId, isGlobal]);

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') handleSearch();
    if (e.key === 'Escape') {
      setQuery('');
      setResults(null);
    }
  };

  return (
    <>
      <div className="search-bar">
        <input
          type="text"
          placeholder="Search documents..."
          value={query}
          onChange={e => setQuery(e.target.value)}
          onKeyDown={handleKeyDown}
        />
      </div>
      <div className="panel-body">
        {searching && <div className="loading">Searching...</div>}

        {results ? (
          results.length === 0 ? (
            <div className="empty">No results</div>
          ) : (
            results.map(r => (
              <div
                key={`${r.project_id}/${r.slug}`}
                className="doc-item"
                onClick={() => onSelect({
                  id: 0,
                  project_id: r.project_id,
                  slug: r.slug,
                  title: r.title,
                  doc_type: r.doc_type,
                  tags: null,
                  updated_at: '',
                })}
              >
                <span className="doc-type">{r.doc_type}</span>
                <span className="doc-title">{r.title}</span>
                <span
                  className="doc-tags"
                  dangerouslySetInnerHTML={{ __html: r.snippet }}
                />
              </div>
            ))
          )
        ) : documents.length === 0 ? (
          <div className="empty">No documents</div>
        ) : (
          documents.map(d => (
            <div key={`${d.project_id}/${d.slug}`} className="doc-item" onClick={() => onSelect(d)}>
              <span className="doc-time">{formatTimeAgo(d.updated_at)}</span>
              {isGlobal && <span className="message-project-tag">[{d.project_id}]</span>}
              <span className="doc-type">{d.doc_type}</span>
              <span className="doc-title">{d.title}</span>
              {d.tags && d.tags.length > 0 && (
                <span className="doc-tags">[{d.tags.join(',')}]</span>
              )}
            </div>
          ))
        )}
      </div>
    </>
  );
}
