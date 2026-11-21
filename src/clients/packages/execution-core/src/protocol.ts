import type {
  AgentMode,
  ExecutionCommandRequest,
  ExecutionMessage,
  ExecutionSettingCommandInput,
  HumanResponseCommandInput,
  PermissionMode,
  ResumeCheckpointCommandInput,
  TurnFinishedStatus,
  TurnIdentity,
  TurnPosition,
} from "@agw/execution-types";

export const DEFAULT_AGENT_MODE: AgentMode = "execute";

/** Turn 生命周期控制消息的类型，与服务端 AgwMessageTypes 一致。 */
export const TURN_START_MESSAGE_TYPE = "agw-turn-start";
export const TURN_FINISHED_MESSAGE_TYPE = "agw-turn-finished";
export const STEP_DISCARDED_MESSAGE_TYPE = "agw-step-discarded";

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
    conversationId: setting.conversationId,
    ...(setting.environmentVariables === undefined
      ? {}
      : { environmentVariables: setting.environmentVariables }),
    ...(setting.permissionMode === undefined ? {} : { permissionMode: setting.permissionMode }),
    ...(setting.resultOnly === undefined ? {} : { resultOnly: setting.resultOnly }),
    ...(setting.extensionId === undefined ? {} : { extensionId: setting.extensionId }),
  };
}

export function buildExecCommand<TInput>(request: ExecutionCommandRequest<TInput>) {
  return {
    type: "ExecCommand" as const,
    conversationId: request.conversationId,
    agentId: request.agentId,
    agentType: request.agentType,
    ...(request.turnId ? { turnId: request.turnId } : {}),
    stream: request.stream ?? true,
    input: request.input,
  };
}

export function buildInterruptCommand(turnId?: string, reason?: string) {
  return {
    type: "InterruptCommand" as const,
    ...(turnId ? { turnId } : {}),
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

export function buildSubscribeTurnCommand(turnId: string, cursor?: string | null) {
  return {
    type: "SubscribeTurnCommand" as const,
    turnId,
    ...(cursor ? { cursor } : {}),
  };
}

export function buildHumanResponseCommand(input: HumanResponseCommandInput) {
  return {
    type: "HumanResponseCommand" as const,
    ...(input.turnId ? { turnId: input.turnId } : {}),
    response: input.response,
  };
}

export function buildResumeCheckpointCommand(input: ResumeCheckpointCommandInput) {
  return {
    type: "ResumeCheckpointCommand" as const,
    checkpointOccurrenceId: input.checkpointOccurrenceId,
    resumeTurnId: input.resumeTurnId,
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

/**
 * 读取 Turn 开始、结束消息中的会话 ID 与 Turn ID；连接上没有 Turn 时服务端发出的结束消息只有 status，返回 null。
 * Reads the conversation and turn IDs of a turn start or finish message; a finish message sent without a turn on the connection carries only status and returns null.
 */
export function getTurnIdentity(message: ExecutionMessage): TurnIdentity | null {
  const conversationId = message.additionalProperties?.conversationId;
  const turnId = message.additionalProperties?.turnId;
  return typeof conversationId === "string" &&
    conversationId.length > 0 &&
    typeof turnId === "string" &&
    turnId.length > 0
    ? { conversationId, turnId }
    : null;
}

/**
 * 服务端回放生命周期消息时，会话中已有排在该 Turn 之后的 Turn，消息带有 superseded 标记。
 * The server marks a replayed lifecycle message as superseded when its conversation already has a turn ordered after it.
 */
export function isSupersededTurnMessage(message: ExecutionMessage): boolean {
  return message.additionalProperties?.superseded === true;
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
