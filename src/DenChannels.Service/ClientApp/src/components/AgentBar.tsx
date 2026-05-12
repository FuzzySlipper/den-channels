import type { AgentSession } from '../api/types';
import { formatTimeAgo, truncate } from '../utils';

interface Props {
  agents: AgentSession[];
  isGlobal: boolean;
}

export function AgentBar({ agents, isGlobal }: Props) {
  if (agents.length === 0) {
    return <div className="agent-item"><span className="agent-heartbeat">(none)</span></div>;
  }

  return (
    <>
      {agents.map((a, i) => (
        <span key={i} className="agent-item">
          <span className="agent-dot" />
          <span className="agent-name">{truncate(a.agent, isGlobal ? 9 : 14)}</span>
          {isGlobal && <span className="agent-project">@{truncate(a.project_id, 6)}</span>}
          <span className="agent-heartbeat">({formatTimeAgo(a.last_heartbeat)})</span>
        </span>
      ))}
    </>
  );
}
