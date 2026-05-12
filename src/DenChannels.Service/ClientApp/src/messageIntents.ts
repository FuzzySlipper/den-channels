import type { MessageIntent } from './api/types';

export const MESSAGE_INTENT_OPTIONS: Array<{ value: MessageIntent; label: string }> = [
  { value: 'handoff', label: 'Handoff' },
  { value: 'review_feedback', label: 'Review Feedback' },
  { value: 'review_approval', label: 'Review Approval' },
  { value: 'review_request', label: 'Review Request' },
  { value: 'question', label: 'Question' },
  { value: 'task_blocked', label: 'Task Blocked' },
  { value: 'task_ready', label: 'Task Ready' },
  { value: 'status_update', label: 'Status Update' },
  { value: 'note', label: 'Note' },
  { value: 'answer', label: 'Answer' },
  { value: 'general', label: 'General' },
];

const INTENT_LABELS: Record<MessageIntent, string> = {
  general: 'General',
  note: 'Note',
  status_update: 'Status',
  question: 'Question',
  answer: 'Answer',
  handoff: 'Handoff',
  review_request: 'Review',
  review_feedback: 'Feedback',
  review_approval: 'Approved',
  task_ready: 'Ready',
  task_blocked: 'Blocked',
};

export function messageIntentLabel(intent: MessageIntent): string {
  return INTENT_LABELS[intent];
}
