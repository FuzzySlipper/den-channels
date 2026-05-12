import type { ReviewFinding } from './api/types';

export interface FindingDisplayNote {
  label: 'Status note' | 'Response';
  value: string;
}

export function currentFindingDisplayNote(finding: ReviewFinding): FindingDisplayNote | null {
  const statusNote = finding.status_notes?.trim();
  if (statusNote) return { label: 'Status note', value: finding.status_notes! };

  const responseNote = finding.response_notes?.trim();
  if (!responseNote) return null;

  if (!finding.status_updated_at) return { label: 'Response', value: finding.response_notes! };

  if (isResponseNewerThanStatus(finding)) return { label: 'Response', value: finding.response_notes! };

  if (finding.status === 'claimed_fixed' && finding.status_updated_by === finding.response_by) {
    return { label: 'Response', value: finding.response_notes! };
  }

  return null;
}

export function renderFindingMeta(finding: ReviewFinding): string[] {
  const parts: string[] = [];
  if (finding.file_references?.length) parts.push(`Files: ${finding.file_references.join(', ')}`);
  if (finding.test_commands?.length) parts.push(`Tests: ${finding.test_commands.join(', ')}`);
  const displayNote = currentFindingDisplayNote(finding);
  if (displayNote) parts.push(`${displayNote.label}: ${displayNote.value}`);
  return parts;
}

function isResponseNewerThanStatus(finding: ReviewFinding): boolean {
  if (!finding.response_at || !finding.status_updated_at) return false;
  const responseAt = Date.parse(finding.response_at);
  const statusAt = Date.parse(finding.status_updated_at);
  return Number.isFinite(responseAt) && Number.isFinite(statusAt) && responseAt > statusAt;
}
