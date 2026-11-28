import type { AgentMode, ExecutionMessage, PermissionStatus } from "@agw/execution-types";
import { getMessageTextContent } from "./message";
import { getPermissionStatus } from "./permissions";
import { getAgentMode, isPositiveSafeInteger, readNonEmptyString } from "./protocol";

export type AgentflowNodeStatus = "running" | "waiting" | "completed" | "failed";

export type AgentflowNodeStatusEvent = {
  type: "agentflow-node-status";
  nodeId: string;
  status: AgentflowNodeStatus;
  interactionId?: string;
};

export type AgentflowNodeSnapshotEvent = {
  type: "agentflow-node-snapshot";
  turnId: string;
  agentflowId: string;
  agentflowRevision: number;
  statuses: AgentflowNodeStatusEvent[];
};

export type ExecutionStatusEvent =
  | AgentflowNodeStatusEvent
  | AgentflowNodeSnapshotEvent
  | ({ type: "permission-status" } & PermissionStatus)
  | { type: "mode-status"; agentId: string; mode: AgentMode }
  | { type: "mode-change-failed"; agentId: string; mode: string; message: string | null };

function readAgentflowNodeStatus(
  nodeIdValue: unknown,
  status: unknown,
  interactionId: unknown,
): AgentflowNodeStatusEvent {
  const nodeId = readNonEmptyString(nodeIdValue);
  if (
    nodeId === null ||
    (status !== "running" && status !== "waiting" && status !== "completed" && status !== "failed")
  ) {
    throw new TypeError("Invalid agentflow-node-status message.");
  }
  return {
    type: "agentflow-node-status",
    nodeId,
    status,
    ...(typeof interactionId === "string" ? { interactionId } : {}),
  };
}

/** 校验 Hub 返回的节点状态快照。Validates the node status snapshot returned by the Hub. */
export function parseAgentflowNodeSnapshot(value: unknown): AgentflowNodeSnapshotEvent {
  if (value === null || typeof value !== "object") {
    throw new TypeError("Invalid Agentflow node status snapshot.");
  }
  const properties = value as Record<string, unknown>;
  const turnId = readNonEmptyString(properties.turnId);
  const agentflowId = readNonEmptyString(properties.agentflowId);
  const { agentflowRevision, statuses } = properties;
  if (
    turnId === null ||
    agentflowId === null ||
    !isPositiveSafeInteger(agentflowRevision) ||
    !Array.isArray(statuses)
  ) {
    throw new TypeError("Invalid Agentflow node status snapshot.");
  }

  return {
    type: "agentflow-node-snapshot",
    turnId,
    agentflowId,
    agentflowRevision,
    statuses: Array.from(statuses, (entry: unknown) => {
      if (entry === null || typeof entry !== "object") {
        throw new TypeError("Invalid Agentflow node status snapshot entry.");
      }
      const { nodeId, status, interactionId } = entry as Record<string, unknown>;
      if (interactionId != null && typeof interactionId !== "string") {
        throw new TypeError("Invalid Agentflow node status snapshot interactionId.");
      }
      return readAgentflowNodeStatus(nodeId, status, interactionId);
    }),
  };
}

/** 读取状态消息；对话消息返回 null。Reads a status message; conversation messages give null. */
export function getExecutionStatus(message: ExecutionMessage): ExecutionStatusEvent | null {
  const properties = message.additionalProperties;
  switch (properties?.type) {
    case "agentflow-node-status": {
      const { nodeId, status, interactionId } = properties;
      return readAgentflowNodeStatus(nodeId, status, interactionId);
    }
    case "permission-status": {
      const status = getPermissionStatus(message);
      return status ? { type: "permission-status", ...status } : null;
    }
    case "mode-status": {
      const mode = getAgentMode(message);
      return mode && typeof properties.agentId === "string"
        ? { type: "mode-status", agentId: properties.agentId, mode }
        : null;
    }
    case "mode-change-failed":
      return typeof properties.agentId === "string" && typeof properties.mode === "string"
        ? {
            type: "mode-change-failed",
            agentId: properties.agentId,
            mode: properties.mode,
            message: getMessageTextContent(message).trim() || null,
          }
        : null;
    default:
      return null;
  }
}
