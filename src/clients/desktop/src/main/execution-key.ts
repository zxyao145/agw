type ExecutionKeyParts = {
  serverId: string;
  projectId: string;
  contextId: string;
};

export function getExecutionKey(parts: ExecutionKeyParts): string {
  return `${parts.serverId}:${parts.projectId}:${parts.contextId}`;
}
