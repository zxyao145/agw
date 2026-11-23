import type { AgentMode, ExecutionMessage, PermissionStatus } from "@agw/execution-types";
import { getMessageTextContent } from "./message";
import { getPermissionStatus } from "./permissions";
import { getAgentMode } from "./protocol";

export type AgentflowNodeStatus = "running" | "waiting" | "completed" | "failed";

export type ExecutionStatusEvent =
  | {
      type: "agentflow-node-status";
      nodeId: string;
      status: AgentflowNodeStatus;
      interactionId?: string;
    }
  | ({ type: "permission-status" } & PermissionStatus)
  | { type: "mode-status"; agentId: string; mode: AgentMode }
  | { type: "mode-change-failed"; agentId: string; mode: string; message: string | null };

/** 读取状态消息；对话消息返回 null。Reads a status message; conversation messages give null. */
export function getExecutionStatus(message: ExecutionMessage): ExecutionStatusEvent | null {
  const properties = message.additionalProperties;
  switch (properties?.type) {
    case "agentflow-node-status": {
      const { nodeId, status, interactionId } = properties;
      if (
        typeof nodeId !== "string" ||
        nodeId.length === 0 ||
        (status !== "running" &&
          status !== "waiting" &&
          status !== "completed" &&
          status !== "failed")
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
