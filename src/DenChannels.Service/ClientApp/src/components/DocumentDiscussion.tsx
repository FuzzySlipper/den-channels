import { useCallback, useEffect, useRef, useState } from 'react';
import type { DocumentSummary, DiscussionComment } from '../api/types';
import { getDocumentDiscussion, postDocumentDiscussionComment } from '../api/client';

interface Props {
  summary: DocumentSummary;
}

export function DocumentDiscussion({ summary }: Props) {
  const [comments, setComments] = useState<DiscussionComment[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [posting, setPosting] = useState(false);
  const [postError, setPostError] = useState<string | null>(null);
  const [author, setAuthor] = useState('den-web');
  const [content, setContent] = useState('');
  const [replyTo, setReplyTo] = useState<number | null>(null);
  const listEndRef = useRef<HTMLDivElement>(null);

  const loadDiscussion = useCallback(() => {
    setLoading(true);
    setLoadError(null);

    getDocumentDiscussion(summary.project_id, summary.slug)
      .then(discussion => {
        if (discussion) {
          setComments(discussion.comments);
        } else {
          setComments([]);
        }
      })
      .catch(error => {
        setLoadError(error instanceof Error ? error.message : String(error));
      })
      .finally(() => {
        setLoading(false);
      });
  }, [summary.project_id, summary.slug]);

  useEffect(() => {
    loadDiscussion();
  }, [loadDiscussion]);

  const handlePost = useCallback(async () => {
    const trimmed = content.trim();
    if (!trimmed || posting) return;

    setPosting(true);
    setPostError(null);

    try {
      const comment = await postDocumentDiscussionComment(
        summary.project_id,
        summary.slug,
        {
          author_identity: author.trim() || 'den-web',
          body_markdown: trimmed,
          parent_comment_id: replyTo,
          comment_kind: 'comment',
        },
      );

      setComments((prev: DiscussionComment[]) => [...prev, comment]);
      setContent('');
      setReplyTo(null);
    } catch (error) {
      setPostError(error instanceof Error ? error.message : String(error));
    } finally {
      setPosting(false);
    }
  }, [content, posting, summary.project_id, summary.slug, author, replyTo]);

  // Scroll to bottom after new comments
  useEffect(() => {
    if (!loading) {
      listEndRef.current?.scrollIntoView({ behavior: 'smooth' });
    }
  }, [comments.length, loading]);

  const rootComments = comments.filter(c => c.parent_comment_id === null);
  const repliesByParent = new Map<number, DiscussionComment[]>();
  for (const c of comments) {
    if (c.parent_comment_id !== null) {
      const existing = repliesByParent.get(c.parent_comment_id) ?? [];
      existing.push(c);
      repliesByParent.set(c.parent_comment_id, existing);
    }
  }

  function formatTime(iso: string): string {
    try {
      return new Date(iso + 'Z').toLocaleString();
    } catch {
      return iso;
    }
  }

  function renderComment(comment: DiscussionComment, isReply: boolean) {
    const replies = repliesByParent.get(comment.id) ?? [];
    return (
      <div
        key={comment.id}
        className={`discussion-comment${isReply ? ' discussion-reply' : ''}`}
      >
        <div className="discussion-comment-header">
          <span className="discussion-comment-author">{comment.author_identity}</span>
          <span className="discussion-comment-time">{formatTime(comment.created_at)}</span>
        </div>
        <div className="discussion-comment-body">{comment.body_markdown}</div>
        <div className="discussion-comment-actions">
          <button
            type="button"
            className="discussion-reply-button"
            onClick={() => setReplyTo(replyTo === comment.id ? null : comment.id)}
          >
            {replyTo === comment.id ? 'Cancel reply' : 'Reply'}
          </button>
        </div>
        {replies.map(r => renderComment(r, true))}
      </div>
    );
  }

  return (
    <div className="detail-section discussion-panel">
      <div className="detail-section-header">
        <h3>Discussion</h3>
        {!loading && <span className="detail-subtle">{comments.length > 0 ? `${comments.length} comment${comments.length !== 1 ? 's' : ''}` : 'No comments yet'}</span>}
      </div>

      <div className="discussion-info" role="status">
        Discussion threads are separate from the canonical document content.
        Comments are not part of the document Markdown body, summaries, or channels.
        Mentions are rendered as plain display metadata in Phase 1.
      </div>

      {loadError && (
        <div className="detail-error" role="alert">
          Failed to load discussion: {loadError}
        </div>
      )}

      {loading ? (
        <div className="loading">Loading discussion...</div>
      ) : comments.length === 0 && !loadError ? (
        <div className="discussion-empty">
          <span className="empty-inline">No discussion comments yet. Start the conversation below.</span>
        </div>
      ) : (
        <div className="discussion-comments-list">
          {rootComments.map(c => renderComment(c, false))}
          <div ref={listEndRef} />
        </div>
      )}

      <div className="discussion-composer">
        {postError && (
          <div className="detail-error discussion-post-error" role="alert">
            Post failed: {postError}
          </div>
        )}
        {replyTo !== null && (
          <div className="discussion-reply-context" role="status">
            Replying to comment #{replyTo}
            <button
              type="button"
              className="discussion-reply-cancel"
              onClick={() => setReplyTo(null)}
            >
              ✕
            </button>
          </div>
        )}
        <div className="discussion-composer-row">
          <input
            className="discussion-composer-author"
            type="text"
            value={author}
            onChange={e => setAuthor(e.target.value)}
            placeholder="Author identity"
            disabled={posting}
            aria-label="Comment author"
          />
        </div>
        <textarea
          className="discussion-composer-input"
          value={content}
          onChange={e => setContent(e.target.value)}
          placeholder={replyTo !== null ? 'Write a reply...' : 'Write a comment...'}
          disabled={posting}
          rows={3}
          aria-label="Comment content"
        />
        <div className="discussion-composer-actions">
          <button
            className="detail-action detail-action-primary"
            onClick={() => void handlePost()}
            disabled={!content.trim() || posting}
          >
            {posting ? 'Posting...' : 'Post comment'}
          </button>
        </div>
      </div>
    </div>
  );
}
