import type { ExecutionKeyParts } from "@agw/desktop-contracts";

export function getExecutionKey(parts: ExecutionKeyParts): string {
  return `${parts.serverId}:${parts.projectId}:${parts.contextId}`;
}
