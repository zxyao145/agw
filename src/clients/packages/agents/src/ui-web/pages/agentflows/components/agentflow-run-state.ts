import type { AiMessage } from "@agw/api";
import {
  getMessageTextContent,
  getTurnFinishedStatus,
  isTurnStartMessage,
  mergeStreamingMessage,
  type ExecutionStatusEvent,
  type AgentflowNodeStatus,
} from "@agw/execution-core";

export type AgentflowRunState = {
  turnActive: boolean;
  statusByNodeId: Record<string, "running" | "completed" | "failed">;
  pendingInteractionIdsByNodeId: Record<string, string[]>;
  activeMemberIdByNodeId: Record<string, string>;
  latestOutputByNodeId: Record<string, { messageId: string; text: string }>;
  nodeIdByMessageId: Record<string, string>;
};

export const EMPTY_RUN_STATE: AgentflowRunState = {
  turnActive: false,
  statusByNodeId: {},
  pendingInteractionIdsByNodeId: {},
  activeMemberIdByNodeId: {},
  latestOutputByNodeId: {},
  nodeIdByMessageId: {},
};

export function applyAgentflowRunStatus(
  state: AgentflowRunState,
  status: ExecutionStatusEvent,
): AgentflowRunState {
  if (status.type !== "agentflow-node-status") return state;

  if (status.status === "waiting") {
    if (!status.interactionId) return state;
    const interactionIds = state.pendingInteractionIdsByNodeId[status.nodeId] ?? [];
    return {
      ...state,
      pendingInteractionIdsByNodeId: {
        ...state.pendingInteractionIdsByNodeId,
        [status.nodeId]: interactionIds.includes(status.interactionId)
          ? interactionIds
          : [...interactionIds, status.interactionId],
      },
    };
  }

  const pendingInteractionIds = state.pendingInteractionIdsByNodeId[status.nodeId] ?? [];
  const nextPendingIds = status.interactionId
    ? pendingInteractionIds.filter((interactionId) => interactionId !== status.interactionId)
    : pendingInteractionIds;
  const pendingInteractionIdsByNodeId = { ...state.pendingInteractionIdsByNodeId };
  if (nextPendingIds.length > 0) pendingInteractionIdsByNodeId[status.nodeId] = nextPendingIds;
  else delete pendingInteractionIdsByNodeId[status.nodeId];

  return {
    ...state,
    statusByNodeId: { ...state.statusByNodeId, [status.nodeId]: status.status },
    pendingInteractionIdsByNodeId,
  };
}

export function applyAgentflowRunMessage(
  state: AgentflowRunState,
  message: AiMessage,
): AgentflowRunState {
  if (isTurnStartMessage(message)) return { ...EMPTY_RUN_STATE, turnActive: true };
  if (getTurnFinishedStatus(message)) {
    return {
      ...state,
      turnActive: false,
      statusByNodeId: Object.fromEntries(
        Object.entries(state.statusByNodeId).filter(([, status]) => status !== "running"),
      ),
      pendingInteractionIdsByNodeId: {},
      activeMemberIdByNodeId: {},
    };
  }

  const nodeId = message.additionalProperties?.nodeId;
  if (typeof nodeId !== "string") return state;

  const nodeIdByMessageId = { ...state.nodeIdByMessageId, [message.messageId]: nodeId };
  const previousOutput = state.latestOutputByNodeId[nodeId];
  const text =
    previousOutput?.messageId === message.messageId
      ? getMessageTextContent(
          mergeStreamingMessage(
            [
              {
                messageId: previousOutput.messageId,
                contents: [{ type: "TextContent", content: previousOutput.text }],
              },
            ],
            message,
          )[0],
        )
      : getMessageTextContent(message);
  const latestOutputByNodeId = {
    ...state.latestOutputByNodeId,
    [nodeId]: { messageId: message.messageId, text },
  };

  const nodePath = message.additionalProperties?.nodePath;
  const memberId = typeof nodePath === "string" ? getMemberId(nodeId, nodePath) : null;
  const activeMemberIdByNodeId = memberId
    ? { ...state.activeMemberIdByNodeId, [nodeId]: memberId }
    : state.activeMemberIdByNodeId;

  return { ...state, nodeIdByMessageId, latestOutputByNodeId, activeMemberIdByNodeId };
}

export function getNodeRunStatus(
  state: AgentflowRunState,
  nodeId: string,
): AgentflowNodeStatus | null {
  if ((state.pendingInteractionIdsByNodeId[nodeId]?.length ?? 0) > 0) return "waiting";
  return state.statusByNodeId[nodeId] ?? null;
}

function getMemberId(nodeId: string, nodePath: string): string | null {
  const pathNodeId = nodePath.split("/").at(-1);
  const memberPrefix = `${nodeId}.`;
  if (!pathNodeId?.startsWith(memberPrefix)) return null;
  const memberId = pathNodeId.slice(memberPrefix.length).replace(/\.manager$/, "");
  return memberId || null;
}
