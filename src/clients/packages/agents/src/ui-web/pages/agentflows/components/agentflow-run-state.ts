import type { AiMessage } from "@agw/api";
import {
  applyAgentflowNodeMessage,
  applyAgentflowNodeStatus,
  EMPTY_AGENTFLOW_NODE_RUN_STATE,
  getAgentflowNodeStatus,
  type AgentflowNodeRunState,
} from "@agw/chat-core";
import {
  getMessageTextContent,
  getTurnFinishedStatus,
  isTurnStartMessage,
  mergeStreamingMessage,
  type ExecutionStatusEvent,
  type AgentflowNodeStatus,
} from "@agw/execution-core";

export type AgentflowRunState = AgentflowNodeRunState & {
  latestOutputByNodeId: Record<string, { messageId: string; text: string }>;
  nodeIdByMessageId: Record<string, string>;
};

export const EMPTY_RUN_STATE: AgentflowRunState = {
  ...EMPTY_AGENTFLOW_NODE_RUN_STATE,
  latestOutputByNodeId: {},
  nodeIdByMessageId: {},
};

export function applyAgentflowRunStatus(
  state: AgentflowRunState,
  status: ExecutionStatusEvent,
): AgentflowRunState {
  const nodes = applyAgentflowNodeStatus(state, status);
  return nodes === state ? state : { ...state, ...nodes };
}

export function applyAgentflowRunMessage(
  state: AgentflowRunState,
  message: AiMessage,
): AgentflowRunState {
  const nodes = applyAgentflowNodeMessage(state, message);
  if (isTurnStartMessage(message)) {
    return nodes === state ? state : { ...EMPTY_RUN_STATE, ...nodes };
  }
  const next = nodes === state ? state : { ...state, ...nodes };
  if (getTurnFinishedStatus(message)) return next;

  const nodeId = message.additionalProperties?.nodeId;
  if (typeof nodeId !== "string") return next;

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

  return { ...next, nodeIdByMessageId, latestOutputByNodeId };
}

export function getNodeRunStatus(
  state: AgentflowRunState,
  nodeId: string,
): AgentflowNodeStatus | null {
  return getAgentflowNodeStatus(state, nodeId);
}
