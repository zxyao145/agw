import type {
  ApprovalScope,
  ChatEvent,
  ChatMessageEvent,
  ExecutionCommandRequest,
  ExecutionHubMethod,
  ExecutionMessage,
  InteractionResponse,
  PermissionMode,
} from "@agw/execution-types";

const message: ExecutionMessage = {
  messageId: "message-1",
  role: "user",
  contents: [{ type: "TextContent", content: "Continue" }],
};
const command: ExecutionCommandRequest = {
  conversationId: "conversation-1",
  agentId: "agent-1",
  agentType: 0,
  input: message,
};
const response: InteractionResponse = {
  interactionId: "approval-1",
  kind: "tool-approval",
  approved: true,
  scope: "Once",
};

function readExecutionMethod(event: ChatEvent): ExecutionHubMethod | null {
  if (event.kind === "request" && event.channel === "execution") {
    const args: readonly unknown[] = event.request.args;
    void args;
    return event.request.method;
  }
  return null;
}

function readApprovalScope(interaction: InteractionResponse): ApprovalScope | null {
  return interaction.kind === "tool-approval" ? interaction.scope : null;
}

// @ts-expect-error 未知权限值必须被拒绝。Unknown permission values must be rejected.
const invalidPermission: PermissionMode = "unknown";
// @ts-expect-error 工具审批必须提供 approved。Tool approvals must provide approved.
const invalidApproval: InteractionResponse = {
  interactionId: "approval-1",
  kind: "tool-approval",
  scope: "Once",
};
// @ts-expect-error live 消息使用 execution 通道。Live messages use the execution channel.
const invalidChannel: Extract<ChatMessageEvent, { source: "live" }>["channel"] = "http";

void [
  command,
  response,
  readExecutionMethod,
  readApprovalScope,
  invalidPermission,
  invalidApproval,
  invalidChannel,
];
