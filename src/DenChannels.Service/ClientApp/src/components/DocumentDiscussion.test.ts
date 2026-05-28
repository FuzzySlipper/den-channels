/// <reference types="node" />
import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const cwd = process.cwd();

function readClientSource(...relativeParts: string[]): string {
  return readFileSync(resolve(cwd, 'src', ...relativeParts), 'utf8');
}

describe('Document Discussion panel (#1680)', () => {
  describe('separation from document content', () => {
    it('keeps discussion code in a separate component file', () => {
      expect(readClientSource('components', 'DocumentDiscussion.tsx')).toBeTruthy();
    });

    it('does NOT inject discussion comments into document Markdown content rendering', () => {
      const content = readClientSource('components', 'DocumentDetail.tsx');
      expect(content).toContain("panelTab === 'content'");
      expect(content).toContain("panelTab === 'discussion'");
      expect(content).toContain('<DocumentDiscussion summary={summary} />');
    });

    it('does NOT include discussion in the Markdown editor', () => {
      const detail = readClientSource('components', 'DocumentDetail.tsx');
      // The textarea for document editing should not reference discussion
      expect(detail).toContain('document-editor');
      // Save document should not call discussion API
      expect(detail).not.toContain('postDocumentDiscussionComment');
    });
  });

  describe('empty state', () => {
    it('shows empty state message in the discussion component', () => {
      const component = readClientSource('components', 'DocumentDiscussion.tsx');
      expect(component).toContain('No discussion comments yet');
      expect(component).toContain('Start the conversation below');
    });

    it('explains discussion is separate from canonical document content', () => {
      const component = readClientSource('components', 'DocumentDiscussion.tsx');
      expect(component).toContain('separate from the canonical document content');
      expect(component).toContain('not part of the document Markdown body');
    });
  });

  describe('API integration', () => {
    it('fetches discussion via Core GET document discussion route', () => {
      const client = readClientSource('api', 'client.ts');
      expect(client).toContain('getDocumentDiscussion');
      expect(client).toContain('/api/projects/${esc(projectId)}/documents/${esc(slug)}/discussion');
      expect(client).toContain('DocumentDiscussion');
    });

    it('posts comments via Core POST document discussion comments route', () => {
      const client = readClientSource('api', 'client.ts');
      expect(client).toContain('postDocumentDiscussionComment');
      expect(client).toContain('/documents/${esc(slug)}/discussion/comments');
      expect(client).toContain('PostDiscussionCommentRequest');
    });

    it('handles 404 from discussion API returning null', () => {
      const client = readClientSource('api', 'client.ts');
      expect(client).toContain("res.status === 404");
      expect(client).toContain('return null');
    });
  });

  describe('comment rendering', () => {
    it('renders chronologically with author and timestamp', () => {
      const component = readClientSource('components', 'DocumentDiscussion.tsx');
      expect(component).toContain('discussion-comment-author');
      expect(component).toContain('discussion-comment-time');
      expect(component).toContain('discussion-comment-body');
    });

    it('supports reply threading with parent context', () => {
      const component = readClientSource('components', 'DocumentDiscussion.tsx');
      expect(component).toContain('parent_comment_id');
      expect(component).toContain('replyTo');
      expect(component).toContain('discussion-reply');
      expect(component).toContain('Replying to comment');
    });
  });

  describe('API error handling', () => {
    it('displays load errors gracefully', () => {
      const component = readClientSource('components', 'DocumentDiscussion.tsx');
      expect(component).toContain('Failed to load discussion');
      expect(component).toContain('catch(error');
    });

    it('displays post errors gracefully', () => {
      const component = readClientSource('components', 'DocumentDiscussion.tsx');
      expect(component).toContain('Post failed');
      expect(component).toContain('postError');
    });
  });
});
