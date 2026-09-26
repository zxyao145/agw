type ExecutionKeyParts = {
  serverId: string;
  projectId: string;
  conversationId: string;
};

export function getExecutionKey(parts: ExecutionKeyParts): string {
  return `${parts.serverId}:${parts.projectId}:${parts.conversationId}`;
}
