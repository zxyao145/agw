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
  /** 连接所属的对话；客户端生成，首次受理时由服务端保存。 */
  conversationId: string;
  environmentVariables?: Record<string, string> | null;
  permissionMode?: PermissionMode | null;
  /** 只接收 result 消息，并拒绝本轮的人机交互。 */
  resultOnly?: boolean | null;
  /**
   * 连接代表的 Desktop extension；只由宿主为 extension 的会话写入，宿主自己的会话不带。
   * The Desktop extension the connection represents; written only by the host for extension sessions and omitted by host sessions.
   */
  extensionId?: string;
};

export type ExecutionCommandRequest<TInput = ExecutionUserInput> = {
  conversationId: string;
  agentId: string;
  agentType: number;
  turnId?: string;
  stream?: boolean;
  input: TInput;
};

export type TurnFinishedStatus = "completed" | "interrupted" | "failed";

/** Turn 生命周期控制消息的类型，与服务端 AgwMessageTypes 一致。 */
export const TURN_START_MESSAGE_TYPE = "agw-turn-start";
export const TURN_FINISHED_MESSAGE_TYPE = "agw-turn-finished";
export const STEP_DISCARDED_MESSAGE_TYPE = "agw-step-discarded";
/** Steer 的下发控制消息类型：已送达进入 Turn，或未送达退回。Steer control message types: delivered into the turn, or returned undelivered. */
export const STEER_DELIVERED_MESSAGE_TYPE = "steer-delivered";
export const STEER_RETURNED_MESSAGE_TYPE = "steer-returned";

/** SteerCommand 被 Hub 拒绝时 HubException 的七位错误码。The seven-digit HubException codes a rejected SteerCommand carries. */
export const STEER_ERROR_CODES = {
  invalidArgument: "4000001",
  notSupported: "4000077",
  conversationReadOnly: "4030008",
  noActiveTurn: "4090027",
  limitExceeded: "4090028",
} as const;

/** 服务端给 Turn 内每条消息加上的 Turn ID 与 Turn 内序号；序号用作断线恢复的游标。 */
export type TurnPosition = {
  turnId: string;
  turnSequence: number;
};

/** Turn 生命周期消息所属的会话与 Turn。The conversation and turn of a turn lifecycle message. */
export type TurnIdentity = {
  conversationId: string;
  turnId: string;
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
  turnId?: string;
  response: InteractionResponse;
};

export type SteerCommandInput<TInput = ExecutionUserInput> = {
  conversationId: string;
  /** 客户端正在跟踪的活动 Turn。The active turn the client tracks. */
  turnId: string;
  /** 客户端生成；服务端按它只受理一次，也是送达后用户消息的 messageId。Client generated; the server accepts it once, and it is the delivered user message's messageId. */
  steerId: string;
  input: TInput;
};

export type ResumeCheckpointCommandInput = {
  checkpointOccurrenceId: string;
  resumeTurnId: string;
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

export function buildSteerCommand<TInput>(input: SteerCommandInput<TInput>) {
  return {
    type: "SteerCommand" as const,
    conversationId: input.conversationId,
    turnId: input.turnId,
    steerId: input.steerId,
    input: input.input,
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

/** steer-delivered 控制消息携带的内容。What a steer-delivered control message carries. */
export type SteerDelivered = {
  steerId: string;
  turnId: string;
  stepIndex: number | null;
};

/** steer-returned 控制消息携带的内容；message.contents 是原始输入。What a steer-returned control message carries; message.contents is the original input. */
export type SteerReturned = {
  steerId: string;
  turnId: string;
  reason: string | null;
};

function readNonEmptyString(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

/** 读取 steer-delivered 控制消息；其他消息返回 null。Reads a steer-delivered control message; other messages return null. */
export function getSteerDelivered(message: ExecutionMessage): SteerDelivered | null {
  const properties = message.additionalProperties;
  if (properties?.type !== STEER_DELIVERED_MESSAGE_TYPE) return null;
  const steerId = readNonEmptyString(properties.steerId);
  const turnId = readNonEmptyString(properties.turnId);
  if (!steerId || !turnId) return null;
  const stepIndex = properties.stepIndex;
  return {
    steerId,
    turnId,
    stepIndex: typeof stepIndex === "number" && Number.isSafeInteger(stepIndex) ? stepIndex : null,
  };
}

/** 读取 steer-returned 控制消息；其他消息返回 null。Reads a steer-returned control message; other messages return null. */
export function getSteerReturned(message: ExecutionMessage): SteerReturned | null {
  const properties = message.additionalProperties;
  if (properties?.type !== STEER_RETURNED_MESSAGE_TYPE) return null;
  const steerId = readNonEmptyString(properties.steerId);
  const turnId = readNonEmptyString(properties.turnId);
  if (!steerId || !turnId) return null;
  return { steerId, turnId, reason: readNonEmptyString(properties.reason) };
}

/**
 * 送达后进入 Turn 的 Steer 用户消息：role 为 user 且带有 steerId，messageId 等于 steerId。
 * The user message of a delivered Steer: role user with a steerId, its messageId equal to the steerId.
 */
export function isSteerUserMessage(message: ExecutionMessage): boolean {
  return (
    message.role === "user" && readNonEmptyString(message.additionalProperties?.steerId) !== null
  );
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
