import type { ExecutionMessage } from "./types";

export type PermissionMode = "fullAccess" | "alwaysAsk" | "allowSameArguments";
export type AgentMode = "plan" | "execute";

export const DEFAULT_AGENT_MODE: AgentMode = "execute";

export type ExecutionUserInput<T extends ExecutionMessage = ExecutionMessage> = Pick<
  T,
  "messageId" | "author" | "contents"
>;

export type ExecutionSettingCommandInput = {
  projectId: string;
  contextId?: string | null;
  environmentVariables?: Record<string, string> | null;
  permissionMode?: PermissionMode | null;
};

export type ExecutionCommandRequest<TInput = ExecutionUserInput> = {
  agentId: string;
  agentType: number;
  executionId?: string;
  stream?: boolean;
  input: TInput;
};

export type TurnFinishedStatus = "completed" | "interrupted" | "failed";

/** SignalR 断线后的共享重试间隔；数组耗尽后结束自动重试。 */
export const executionReconnectDelaysMs = [0, 2_000, 5_000, 7_000, 10_000, 20_000, 30_000] as const;

export function getExecutionReconnectDelay(previousRetryCount: number): number | null {
  return executionReconnectDelaysMs[previousRetryCount] ?? null;
}

export function buildSettingCommand(setting: ExecutionSettingCommandInput) {
  return {
    type: "SettingCommand" as const,
    projectId: setting.projectId,
    ...(setting.contextId === undefined ? {} : { contextId: setting.contextId }),
    ...(setting.environmentVariables === undefined
      ? {}
      : { environmentVariables: setting.environmentVariables }),
    ...(setting.permissionMode === undefined ? {} : { permissionMode: setting.permissionMode }),
  };
}

export function buildExecCommand<TInput>(request: ExecutionCommandRequest<TInput>) {
  return {
    type: "ExecCommand" as const,
    agentId: request.agentId,
    agentType: request.agentType,
    ...(request.executionId ? { executionId: request.executionId } : {}),
    stream: request.stream ?? true,
    input: request.input,
  };
}

export function buildInterruptCommand(executionId?: string, reason?: string) {
  return {
    type: "InterruptCommand" as const,
    ...(executionId ? { executionId } : {}),
    ...(reason === undefined ? {} : { reason }),
  };
}

export function buildSetModeCommand(agentId: string, mode: AgentMode) {
  return {
    type: "SetModeCommand" as const,
    agentId,
    mode,
  };
}

export function buildSetPermissionModeCommand(permissionMode: PermissionMode) {
  return {
    type: "SetPermissionModeCommand" as const,
    permissionMode,
  };
}

export function buildSubscribeExecutionCommand(executionId: string, cursor?: string | null) {
  return {
    type: "SubscribeExecutionCommand" as const,
    executionId,
    ...(cursor ? { cursor } : {}),
  };
}

/** 读取服务端 message 级 turn-finished 标记；未知状态按兼容性的 completed 处理。 */
export function getTurnFinishedStatus(message: ExecutionMessage): TurnFinishedStatus | null {
  if (message.additionalProperties?.type !== "turn-finished") return null;
  const status = message.additionalProperties.status;
  return status === "completed" || status === "interrupted" || status === "failed"
    ? status
    : "completed";
}

export function getAgentMode(message: ExecutionMessage): AgentMode | null {
  const type = message.additionalProperties?.type;
  if (type !== "mode-status" && type !== "tool-mode-status") return null;
  const mode = message.additionalProperties?.mode;
  return mode === "plan" || mode === "execute" ? mode : null;
}

export function getLatestAgentMode(
  messages: readonly ExecutionMessage[],
  fallback: AgentMode = DEFAULT_AGENT_MODE,
): AgentMode {
  for (let index = messages.length - 1; index >= 0; index -= 1) {
    const mode = getAgentMode(messages[index]);
    if (mode) return mode;
  }

  return fallback;
}

export function isModeControlMessage(message: ExecutionMessage): boolean {
  const type = message.additionalProperties?.type;
  return type === "mode-status" || type === "mode-change-failed";
}
