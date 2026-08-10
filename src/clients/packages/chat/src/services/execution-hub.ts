import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  HttpTransportType,
  LogLevel,
} from "@microsoft/signalr";

import type { AiMessage } from "@agw/api";
import {
  parseHumanInteractionModeChange,
  parseHumanInteractionQuestions,
  type HumanInteractionModeChange,
  type HumanInteractionQuestion,
} from "./human-interaction";

export type ExecutionRuntimeConfig = {
  baseUrl: string;
  token: string | null;
};

let executionRuntime: ExecutionRuntimeConfig = { baseUrl: "", token: null };

export function configureExecutionRuntime(config: ExecutionRuntimeConfig): void {
  executionRuntime = config;
}

export type ExecutionUserInput = Pick<AiMessage, "messageId" | "author" | "contents">;

export type PermissionMode = "fullAccess" | "alwaysAsk" | "allowSameArguments";
export type AgentMode = "plan" | "execute";

export type ExecutionSetting = {
  projectId: string;
  contextId: string;
  environmentVariables?: Record<string, string> | null;
  permissionMode?: PermissionMode;
};

export type ExecutionTarget = {
  agentId: string;
  agentType: number;
};

export type ExecutionRequest = ExecutionTarget & {
  /** 客户端生成的稳定执行标识，用于 durable 启动幂等和断线恢复。 */
  executionId?: string;
  stream?: boolean;
  input: ExecutionUserInput;
};

export type PendingHumanGate = {
  requestType: "human-gate" | "tool-approval" | "human-interaction";
  requestId: string;
  nodeId?: string;
  nodeName?: string;
  mode: string;
  prompt: string;
  inputPreview?: string;
  toolName?: string;
  callId?: string;
  streamingScopeId?: string;
  arguments?: string;
  interactionKind?: string;
  questions?: HumanInteractionQuestion[];
  modeChange?: HumanInteractionModeChange;
};

export type TurnFinishedStatus = "completed" | "interrupted" | "failed";

export type ExecutionHubHandlers = {
  onMessage: (message: AiMessage) => void;
  onError?: (error: Error) => void;
  onClose?: (error?: Error) => void;
};

export function buildExecutionHubOptions(runtime: ExecutionRuntimeConfig = executionRuntime) {
  const baseUrl = runtime.baseUrl.replace(/\/+$/u, "");
  return {
    url: `${baseUrl}/api/hubs/exec`,
    options: {
      transport: HttpTransportType.WebSockets,
      withCredentials: !baseUrl,
      ...(runtime.token ? { accessTokenFactory: async () => runtime.token! } : {}),
    },
  };
}

export function buildSettingCommand(setting: ExecutionSetting) {
  return {
    type: "SettingCommand" as const,
    projectId: setting.projectId,
    contextId: setting.contextId,
    ...(setting.environmentVariables === undefined
      ? {}
      : { environmentVariables: setting.environmentVariables }),
    ...(setting.permissionMode === undefined ? {} : { permissionMode: setting.permissionMode }),
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

export function getAgentMode(message: AiMessage): AgentMode | null {
  const type = message.additionalProperties?.type;
  if (type !== "mode-status" && type !== "tool-mode-status") return null;
  const mode = message.additionalProperties?.mode;
  return mode === "plan" || mode === "execute" ? mode : null;
}

export function isModeControlMessage(message: AiMessage): boolean {
  const type = message.additionalProperties?.type;
  return type === "mode-status" || type === "mode-change-failed";
}

export function buildExecCommand(request: ExecutionRequest) {
  return {
    type: "ExecCommand" as const,
    agentId: request.agentId,
    agentType: request.agentType,
    ...(request.executionId ? { executionId: request.executionId } : {}),
    stream: request.stream ?? true,
    input: request.input,
  };
}

/** 创建重新附着 durable execution 并继续消息回放的 SignalR 命令。 */
export function buildSubscribeExecutionCommand(executionId: string, cursor?: string | null) {
  return {
    type: "SubscribeExecutionCommand" as const,
    executionId,
    ...(cursor ? { cursor } : {}),
  };
}

export function getTurnFinishedStatus(message: AiMessage): TurnFinishedStatus | null {
  if (message.additionalProperties?.type !== "turn-finished") return null;
  const status = message.additionalProperties.status;
  return status === "completed" || status === "interrupted" || status === "failed"
    ? status
    : "completed";
}

function readString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim().length > 0 ? value : undefined;
}

export function getPendingHumanGate(message: AiMessage): PendingHumanGate | null {
  const properties = message.additionalProperties;
  if (!properties) return null;
  const requestType = properties.type;
  const requestId = readString(properties.requestId);
  if (!requestId) return null;

  if (requestType === "human-interaction-request") {
    const interactionKind = readString(properties.interactionKind);
    if (!interactionKind) return null;
    const questions =
      interactionKind === "questions"
        ? (parseHumanInteractionQuestions(properties.payload) ?? undefined)
        : undefined;
    const modeChange =
      interactionKind === "mode-change"
        ? (parseHumanInteractionModeChange(properties.payload) ?? undefined)
        : undefined;
    return {
      requestType: "human-interaction",
      requestId,
      mode: "interaction",
      interactionKind,
      prompt:
        readString(properties.prompt) ??
        readString(message.contents[0]?.content) ??
        "The agent needs your input to continue.",
      toolName: readString(properties.toolName),
      callId: readString(properties.callId),
      ...(questions ? { questions } : {}),
      ...(modeChange ? { modeChange } : {}),
    };
  }

  if (requestType !== "human-gate-request" && requestType !== "tool-approval-request") return null;
  const nodeId = readString(properties.nodeId);
  if (!nodeId) return null;

  return {
    requestType: requestType === "tool-approval-request" ? "tool-approval" : "human-gate",
    requestId,
    nodeId,
    nodeName: readString(properties.nodeName),
    mode: readString(properties.mode) ?? "approval",
    prompt:
      readString(properties.prompt) ??
      readString(message.contents[0]?.content) ??
      "Human approval is required to continue.",
    inputPreview: readString(properties.inputPreview),
    toolName: readString(properties.toolName),
    arguments: readString(properties.arguments),
  };
}

const executionInterruptTimeoutMs = 3_000;

/** 浏览器为当前服务端、项目和上下文保存的 durable attachment。 */
type PersistedDurableExecution = {
  /** 尚未确认结束的业务执行标识。 */
  executionId: string;
  /** 客户端最后处理完成的 Redis Stream cursor。 */
  cursor: string | null;
};

/** 服务端声明的执行恢复能力；null 表示旧服务端未提供能力接口。 */
type ExecutionProviderCapability = "in-process" | "distributed" | null;

/** 为一个服务端上的项目会话生成互不冲突的 durable attachment 存储键。 */
function getDurableExecutionStorageKey(
  runtime: ExecutionRuntimeConfig,
  setting: ExecutionSetting,
): string {
  const server = runtime.baseUrl.replace(/\/+$/u, "") || "local";
  return `agw:durable-execution:v1:${JSON.stringify([
    server,
    setting.projectId,
    setting.contextId,
  ])}`;
}

/** 从浏览器存储读取并最低限度校验 durable attachment。 */
function readPersistedDurableExecution(key: string): PersistedDurableExecution | null {
  try {
    const value = globalThis.localStorage?.getItem(key);
    if (!value) return null;
    const parsed = JSON.parse(value) as Partial<PersistedDurableExecution>;
    return typeof parsed.executionId === "string"
      ? {
          executionId: parsed.executionId,
          cursor: typeof parsed.cursor === "string" ? parsed.cursor : null,
        }
      : null;
  } catch {
    return null;
  }
}

/** 尽力写入或清除 durable attachment；存储不可用不应阻断聊天。 */
function writePersistedDurableExecution(
  key: string | undefined,
  value: PersistedDurableExecution | null,
): void {
  if (!key) return;
  try {
    if (value) globalThis.localStorage?.setItem(key, JSON.stringify(value));
    else globalThis.localStorage?.removeItem(key);
  } catch {
    // Safari 隐私模式等环境可能禁用存储；这只会失去刷新恢复能力。
  }
}

export async function waitForExecutionTerminal(
  interrupt: Promise<void>,
  turnFinished: Promise<void>,
  timeoutMs = executionInterruptTimeoutMs,
): Promise<void> {
  let timeoutId: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timeoutId = setTimeout(
      () => reject(new Error("Timed out waiting for execution to stop.")),
      timeoutMs,
    );
  });

  try {
    await Promise.race([interrupt.then(() => turnFinished), timeout]);
  } finally {
    if (timeoutId !== undefined) clearTimeout(timeoutId);
  }
}

export class ExecutionHubClient {
  private readonly connection: HubConnection;
  /** 生成持久化隔离键所需的当前服务端配置。 */
  private readonly runtime: ExecutionRuntimeConfig;
  private handlers: ExecutionHubHandlers;
  private disposed = false;
  private hasActiveTurn = false;
  private readonly turnFinishedWaiters = new Set<() => void>();
  /** 重连后必须先恢复的服务端执行设置。 */
  private setting: ExecutionSetting | null = null;
  /** 当前项目会话对应的浏览器存储键。 */
  private durableStorageKey: string | undefined;
  /** 当前启动或附着的 durable execution。 */
  private activeExecutionId: string | null = null;
  /** 当前客户端已经消费到的 Redis Stream cursor。 */
  private streamCursor: string | null = null;
  /** 标记 executionId 已被 durable 服务端确认，断线时不得误判执行结束。 */
  private durableConfirmed = false;
  /** 服务端选择的执行提供程序能力。 */
  private executionProvider: ExecutionProviderCapability = null;

  public constructor(
    handlers: ExecutionHubHandlers,
    runtime: ExecutionRuntimeConfig = executionRuntime,
  ) {
    this.handlers = handlers;
    this.runtime = runtime;
    const hub = buildExecutionHubOptions(runtime);
    this.connection = new HubConnectionBuilder()
      .withUrl(hub.url, hub.options)
      .configureLogging(LogLevel.Warning)
      .withAutomaticReconnect()
      .build();

    this.connection.on("ReceiveMessage", (message: AiMessage) => {
      this.updateDurableProgress(message);
      if (message.additionalProperties?.type === "turn-start") {
        this.hasActiveTurn = true;
      } else if (getTurnFinishedStatus(message)) {
        this.finishActiveTurn();
      }

      if (!this.disposed) this.handlers.onMessage(message);
    });
    this.connection.onclose((error) => {
      if (this.durableConfirmed) this.hasActiveTurn = false;
      else this.finishActiveTurn();
      if (!this.disposed) this.handlers.onClose?.(error);
    });
    this.connection.onreconnected(() => {
      void this.restoreAfterReconnect().catch((error) => {
        const normalized = error instanceof Error ? error : new Error(String(error));
        this.handlers.onError?.(normalized);
      });
    });
  }

  public setHandlers(handlers: ExecutionHubHandlers): void {
    this.handlers = handlers;
  }

  public async configure(setting: ExecutionSetting): Promise<void> {
    this.setting = setting;
    this.durableStorageKey = getDurableExecutionStorageKey(this.runtime, setting);
    const persisted = readPersistedDurableExecution(this.durableStorageKey);
    if (persisted) {
      this.activeExecutionId = persisted.executionId;
      this.streamCursor = persisted.cursor;
      this.durableConfirmed = true;
      this.hasActiveTurn = true;
    }
    await this.ensureConnected();
    await this.refreshExecutionProvider();
    await this.dispatch(buildSettingCommand(setting));
    if (persisted) {
      if (this.executionProvider === "in-process") {
        this.finishActiveTurn();
      } else {
        await this.restoreDurableSubscription(persisted.executionId, persisted.cursor);
      }
    }
  }

  public async execute(request: ExecutionRequest): Promise<void> {
    const executionId = request.executionId ?? globalThis.crypto.randomUUID();
    this.activeExecutionId = executionId;
    this.streamCursor = null;
    this.durableConfirmed = this.executionProvider === "distributed";
    this.hasActiveTurn = true;
    if (this.durableConfirmed) {
      writePersistedDurableExecution(this.durableStorageKey, {
        executionId,
        cursor: null,
      });
    }
    try {
      await this.dispatch(buildExecCommand({ ...request, executionId }));
    } catch (error) {
      if (!this.durableConfirmed) {
        this.finishActiveTurn();
      } else if (this.connection.state === HubConnectionState.Connected) {
        try {
          if (await this.restoreDurableSubscription(executionId, null)) return;
        } catch {
          // 短暂探测失败不能证明服务端未接受启动，因此保留 executionId 供后续恢复。
        }
      }
      throw error;
    }
  }

  public async setMode(agentId: string, mode: AgentMode): Promise<void> {
    await this.dispatch(buildSetModeCommand(agentId, mode));
  }

  public async setPermissionMode(permissionMode: PermissionMode): Promise<void> {
    await this.dispatch(buildSetPermissionModeCommand(permissionMode));
  }

  public async interrupt(reason?: string): Promise<void> {
    await this.dispatch({
      type: "InterruptCommand",
      ...(this.activeExecutionId ? { executionId: this.activeExecutionId } : {}),
      reason,
    });
  }

  public async interruptAndWait(reason?: string): Promise<void> {
    if (!this.hasActiveTurn) {
      await this.interrupt(reason);
      return;
    }

    let resolveTurnFinished!: () => void;
    const turnFinished = new Promise<void>((resolve) => {
      resolveTurnFinished = resolve;
      this.turnFinishedWaiters.add(resolve);
    });

    try {
      await waitForExecutionTerminal(this.interrupt(reason), turnFinished);
    } finally {
      this.turnFinishedWaiters.delete(resolveTurnFinished);
    }
  }

  public async submitHumanResponse(args: {
    requestId: string;
    approved: boolean;
    responseText?: string | null;
    approvalScope?: "once" | "always-tool" | "always-arguments";
    responseData?: unknown;
  }): Promise<void> {
    await this.dispatch({
      type: "HumanResponseCommand",
      ...(this.activeExecutionId ? { executionId: this.activeExecutionId } : {}),
      ...args,
    });
  }

  public async dispose(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    if (this.connection.state !== HubConnectionState.Disconnected) {
      await this.connection.stop();
    }
  }

  private async dispatch(command: unknown): Promise<void> {
    try {
      await this.ensureConnected();
      await this.connection.invoke("DispatchCommand", command);
    } catch (error) {
      const normalized = error instanceof Error ? error : new Error(String(error));
      this.handlers.onError?.(normalized);
      throw normalized;
    }
  }

  private async ensureConnected(): Promise<void> {
    if (this.disposed) throw new Error("Execution connection is disposed");
    if (this.connection.state === HubConnectionState.Connected) return;
    if (this.connection.state !== HubConnectionState.Disconnected) {
      throw new Error(`Execution connection is ${this.connection.state}`);
    }
    await this.connection.start();
  }

  /** SignalR 自动重连后恢复设置，再按服务端能力重新附着 durable execution。 */
  private async restoreAfterReconnect(): Promise<void> {
    if (this.disposed || !this.setting) return;
    await this.refreshExecutionProvider();
    await this.connection.invoke("DispatchCommand", buildSettingCommand(this.setting));
    if (this.executionProvider === "in-process") {
      this.finishActiveTurn();
    } else if (this.durableConfirmed && this.activeExecutionId) {
      await this.restoreDurableSubscription(this.activeExecutionId, this.streamCursor);
    }
  }

  /** 尝试重新订阅指定执行；只有明确不存在时才清理本地 attachment。 */
  private async restoreDurableSubscription(
    executionId: string,
    cursor: string | null,
  ): Promise<boolean> {
    try {
      await this.connection.invoke(
        "DispatchCommand",
        buildSubscribeExecutionCommand(executionId, cursor),
      );
      return true;
    } catch (error) {
      const normalized = error instanceof Error ? error : new Error(String(error));
      if (normalized.message.includes("404_0011")) {
        this.finishActiveTurn();
        return false;
      }
      throw normalized;
    }
  }

  /** 探测服务端执行提供程序，同时兼容尚未提供该 Hub 方法的旧版本。 */
  private async refreshExecutionProvider(): Promise<void> {
    try {
      const provider = await this.connection.invoke<string>("GetExecutionProvider");
      this.executionProvider =
        provider.toLowerCase() === "distributed"
          ? "distributed"
          : provider.toLowerCase() === "inprocess"
            ? "in-process"
            : null;
    } catch {
      // 旧服务端没有能力接口时，仍可根据消息中的 executionId 进行确认。
      this.executionProvider = null;
    }
  }

  /** 从服务端消息推进 executionId 与 cursor，并持久化最新恢复位置。 */
  private updateDurableProgress(message: AiMessage): void {
    const executionId = readString(message.additionalProperties?.executionId);
    if (!executionId) return;
    this.activeExecutionId = executionId;
    this.durableConfirmed = true;
    const cursor = readString(message.additionalProperties?.streamCursor);
    if (cursor) this.streamCursor = cursor;
    writePersistedDurableExecution(this.durableStorageKey, {
      executionId,
      cursor: this.streamCursor,
    });
  }

  private finishActiveTurn(): void {
    this.hasActiveTurn = false;
    this.activeExecutionId = null;
    this.streamCursor = null;
    this.durableConfirmed = false;
    writePersistedDurableExecution(this.durableStorageKey, null);
    for (const resolve of this.turnFinishedWaiters) {
      resolve();
    }
    this.turnFinishedWaiters.clear();
  }
}
