import type { ExecutionMessage } from "./types";

export type PermissionMode = "fullAccess" | "alwaysAsk" | "allowSameArguments";
export type AgentMode = "plan" | "execute";

export const DEFAULT_AGENT_MODE: AgentMode = "execute";

export type ExecutionUserInput<T extends ExecutionMessage = ExecutionMessage> = Pick<
  T,
  "messageId" | "author" | "contents" | "createdAt"
>;

export type ExecutionSettingCommandInput = {
  projectId: string;
  contextId?: string | null;
  environmentVariables?: Record<string, string> | null;
  permissionMode?: PermissionMode | null;
  /** 只接收 result 消息，并拒绝本轮的人机交互。 */
  resultOnly?: boolean | null;
};

export type ExecutionCommandRequest<TInput = ExecutionUserInput> = {
  conversationId: string;
  agentId: string;
  agentType: number;
  executionId?: string;
  stream?: boolean;
  input: TInput;
};

export type TurnFinishedStatus = "completed" | "interrupted" | "failed";

/** Turn 生命周期控制消息的类型，与服务端 AgwMessageTypes 一致。 */
export const TURN_START_MESSAGE_TYPE = "agw-turn-start";
export const TURN_FINISHED_MESSAGE_TYPE = "agw-turn-finished";
export const STEP_DISCARDED_MESSAGE_TYPE = "agw-step-discarded";

/** 服务端给 Turn 内每条消息加上的 Turn ID 与 Turn 内序号；序号用作断线恢复的游标。 */
export type TurnPosition = {
  turnId: string;
  turnSequence: number;
};

export type ApprovalScope = "Once" | "AlwaysTool" | "AlwaysArguments";
export type InteractionSource = {
  nodeId?: string;
  nodeName?: string;
  toolName?: string;
  callId?: string;
  providerRequestId?: string;
  /** SDK workflow request port, independent of the business node ID. */
  providerScopeId?: string;
};
type InteractionRequestBase = {
  interactionId: string;
  prompt: string;
  source: InteractionSource;
};
export type InteractionRequest = InteractionRequestBase &
  (
    | { kind: "tool-approval"; arguments?: unknown }
    | { kind: "workflow-gate"; mode: string; inputPreview?: string }
    | { kind: "user-input"; inputKind: string; payload: unknown }
  );
export type InteractionResponse = { interactionId: string } & (
  | { kind: "tool-approval"; approved: boolean; scope: ApprovalScope }
  | { kind: "workflow-gate"; approved: boolean; responseText?: string }
  | { kind: "user-input"; cancelled: boolean; responseData?: unknown }
);
export type HumanResponseCommandInput = {
  executionId?: string;
  response: InteractionResponse;
};

export type ResumeCheckpointCommandInput = {
  checkpointOccurrenceId: string;
  resumeExecutionId: string;
  agentflowId: string;
};

/** SignalR 断线后的基础重试间隔；数组耗尽后结束自动重试。 */
export const executionReconnectDelaysMs = [
  1_000, 2_000, 3_000, 5_000, 8_000, 13_000, 21_000, 34_000, 55_000, 60_000,
] as const;

export const executionSilentReconnectAttempts = 5;

/** 每次调度独立增加 0–20% Jitter；调用方复用返回值进行等待和进度展示。 */
export function getExecutionReconnectDelay(previousRetryCount: number): number | null {
  const baseMs = executionReconnectDelaysMs[previousRetryCount];
  return baseMs === undefined ? null : Math.round(baseMs * (1 + Math.random() * 0.2));
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
    ...(setting.resultOnly === undefined ? {} : { resultOnly: setting.resultOnly }),
  };
}

export function buildExecCommand<TInput>(request: ExecutionCommandRequest<TInput>) {
  return {
    type: "ExecCommand" as const,
    conversationId: request.conversationId,
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

export function buildHumanResponseCommand(input: HumanResponseCommandInput) {
  return {
    type: "HumanResponseCommand" as const,
    ...(input.executionId ? { executionId: input.executionId } : {}),
    response: input.response,
  };
}

export function buildResumeCheckpointCommand(input: ResumeCheckpointCommandInput) {
  return {
    type: "ResumeCheckpointCommand" as const,
    checkpointOccurrenceId: input.checkpointOccurrenceId,
    resumeExecutionId: input.resumeExecutionId,
    agentflowId: input.agentflowId,
  };
}

export function isTurnStartMessage(message: ExecutionMessage): boolean {
  return message.additionalProperties?.type === TURN_START_MESSAGE_TYPE;
}

/** 读取消息所属的 Turn 与它在 Turn 内的序号；不属于 Turn 的消息返回 null。 */
export function getTurnPosition(message: ExecutionMessage): TurnPosition | null {
  const turnId = message.additionalProperties?.turnId;
  const turnSequence = message.additionalProperties?.turnSequence;
  return typeof turnId === "string" &&
    turnId.length > 0 &&
    typeof turnSequence === "number" &&
    Number.isSafeInteger(turnSequence) &&
    turnSequence > 0
    ? { turnId, turnSequence }
    : null;
}

/** 读取服务端 message 级 Turn 结束标记；未知状态按兼容性的 completed 处理。 */
export function getTurnFinishedStatus(message: ExecutionMessage): TurnFinishedStatus | null {
  if (message.additionalProperties?.type !== TURN_FINISHED_MESSAGE_TYPE) return null;
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
