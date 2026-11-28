import type { ChatTargetType } from "@agw/api";
import { EMPTY_AGENTFLOW_NODE_RUN_STATE, type AgentflowNodeRunState } from "@agw/chat-core";

export type AsideAgentflow = {
  agentflowId: string;
  revision: number;
  run: AgentflowNodeRunState;
};

export function selectAsideAgentflow(
  nodeRun: AgentflowNodeRunState,
  target: { id: string; type: ChatTargetType; agentflowRevision?: number } | null,
  agentflows: readonly { id: string; publishedRevision: number }[],
): AsideAgentflow | null {
  const { agentflowId, agentflowRevision, turnActive } = nodeRun;
  if (agentflowId && (turnActive || (target?.type === "agentflow" && target.id === agentflowId))) {
    return agentflowRevision !== null && agentflowRevision > 0
      ? { agentflowId, revision: agentflowRevision, run: nodeRun }
      : null;
  }
  if (target?.type !== "agentflow") return null;

  const revision =
    target.agentflowRevision ?? agentflows.find(({ id }) => id === target.id)?.publishedRevision;
  return revision !== undefined && revision > 0
    ? { agentflowId: target.id, revision, run: EMPTY_AGENTFLOW_NODE_RUN_STATE }
    : null;
}
