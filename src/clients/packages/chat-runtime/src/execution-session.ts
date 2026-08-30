import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  HttpTransportType,
  LogLevel,
  type IHttpConnectionOptions,
  type IRetryPolicy,
} from "@microsoft/signalr";

import type { AiMessage } from "@agw/api";
import {
  buildExecCommand as buildCoreExecCommand,
  buildHumanResponseCommand,
  buildInterruptCommand,
  buildResumeCheckpointCommand,
  buildSetModeCommand,
  buildSetPermissionModeCommand,
  buildSettingCommand as buildCoreSettingCommand,
  buildSubscribeExecutionCommand as buildCoreSubscribeExecutionCommand,
  DEFAULT_AGENT_MODE,
  executionReconnectDelaysMs,
  getAgentMode,
  getExecutionReconnectDelay,
  getLatestAgentMode,
  getMessageStreamingScopeId,
  getTurnFinishedStatus,
  isModeControlMessage,
  isUserTurnMessage,
  scopeStreamingMessage,
  type AgentMode,
  type ExecutionUserInput as CoreExecutionUserInput,
  type PermissionMode as CorePermissionMode,
  type TurnFinishedStatus,
} from "@agw/execution-core";
import {
  getAgentflowCheckpointMessage,
  getPendingHumanGate,
  type AgentflowCheckpointAvailability,
  type PendingHumanGate,
} from "@agw/chat-core";

export { getAgentflowCheckpointMessage, getPendingHumanGate } from "@agw/chat-core";
export type {
  AgentflowCheckpointAvailability,
  AgentflowCheckpointMarkerInfo,
  AgentflowCheckpointMessage,
  PendingHumanGate,
} from "@agw/chat-core";

export type ExecutionRuntimeConfig = {
  baseUrl: string;
  token: string | null;
  withCredentials?: boolean;
  webSocket?: typeof WebSocket;
  attachmentStore?: ExecutionAttachmentStore | null;
};

export type ExecutionAttachmentStore = {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
};

let executionRuntime: ExecutionRuntimeConfig = { baseUrl: "", token: null };

export { executionReconnectDelaysMs, getExecutionReconnectDelay, getTurnFinishedStatus };

/** 描述当前 SignalR 自动重连尝试。 */
export type ExecutionReconnectState = {
  /** 当前处于自动重试，或所有自动重试均已失败。 */
  status: "reconnecting" | "failed";
  /** 即将进行的重连次数，从 1 开始。 */
  retryAttempt: number;
  /** 距离本次重连尝试的等待时间。 */
  retryDelayMs: number;
};

/** 判断 SignalR 是否已经完成最后一次自动重连尝试。 */
export function isExecutionReconnectExhausted(state: ExecutionReconnectState | null): boolean {
  return (
    state?.status === "reconnecting" && state.retryAttempt === executionReconnectDelaysMs.length
  );
}

export function configureExecutionRuntime(config: ExecutionRuntimeConfig): void {
  executionRuntime = config;
}

export type ExecutionUserInput = CoreExecutionUserInput<AiMessage>;

export type PermissionMode = CorePermissionMode;
export {
  buildSetModeCommand,
  buildSetPermissionModeCommand,
  DEFAULT_AGENT_MODE,
  getAgentMode,
  getLatestAgentMode,
  isModeControlMessage,
  isUserTurnMessage,
};
export type { AgentMode };

export type ExecutionSetting = {
  projectId: string;
  contextId: string;
  environmentVariables?: Record<string, string> | null;
  permissionMode?: PermissionMode;
};

export type ExecutionConfigurationResult = {
  restoredDurableExecution: boolean;
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

export type { TurnFinishedStatus };

export type ExecutionHubHandlers = {
  onMessage: (message: AiMessage) => void;
  onError?: (error: Error) => void;
  onClose?: (error?: Error) => void;
  /** SignalR 进入重连，或准备下一次重试时触发。 */
  onReconnecting?: (state: ExecutionReconnectState) => void;
  /** SignalR 已耗尽自动重试，或手动重试仍然失败时触发。 */
  onReconnectFailed?: (state: ExecutionReconnectState) => void;
  /** SignalR 重连并恢复服务端执行上下文后触发。 */
  onReconnected?: () => void;
};

/** 构建固定使用 WebSocket 的 SignalR 连接参数，并按当前运行环境附加 Bearer Token。 */
export function buildExecutionHubOptions(runtime: ExecutionRuntimeConfig = executionRuntime) {
  const baseUrl = runtime.baseUrl.replace(/\/+$/u, "");
  const options: IHttpConnectionOptions & { WebSocket?: typeof WebSocket } = {
    transport: HttpTransportType.WebSockets,
    skipNegotiation: true,
    withCredentials: runtime.withCredentials ?? !baseUrl,
    ...(runtime.token ? { accessTokenFactory: async () => runtime.token! } : {}),
    ...(runtime.webSocket ? { WebSocket: runtime.webSocket } : {}),
  };
  return {
    url: `${baseUrl}/api/hubs/exec`,
    options,
  };
}

export function buildSettingCommand(setting: ExecutionSetting) {
  return buildCoreSettingCommand(setting);
}

export function buildExecCommand(request: ExecutionRequest) {
  return buildCoreExecCommand(request);
}

/** 创建重新附着 durable execution 并继续消息回放的 SignalR 命令。 */
export function buildSubscribeExecutionCommand(executionId: string, cursor?: string | null) {
  return buildCoreSubscribeExecutionCommand(executionId, cursor);
}

function readString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim().length > 0 ? value : undefined;
}

/** 读取服务端持久化的作用域；委托给 platform-neutral 的 execution-core，保持单一来源。 */
export { getMessageStreamingScopeId };

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
export function getDurableExecutionStorageKey(
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
function getAttachmentStore(runtime: ExecutionRuntimeConfig): ExecutionAttachmentStore | null {
  if (runtime.attachmentStore !== undefined) return runtime.attachmentStore;
  const host = globalThis as unknown as { localStorage?: ExecutionAttachmentStore };
  return host.localStorage ?? null;
}

function readPersistedDurableExecution(
  key: string,
  runtime: ExecutionRuntimeConfig,
): PersistedDurableExecution | null {
  try {
    const value = getAttachmentStore(runtime)?.getItem(key);
    if (!value) return null;
    const parsed = JSON.parse(value) as Partial<PersistedDurableExecution>;
    return typeof parsed.executionId === "string" && parsed.executionId.trim().length > 0
      ? {
          executionId: parsed.executionId,
          cursor: typeof parsed.cursor === "string" ? parsed.cursor : null,
        }
      : null;
  } catch {
    return null;
  }
}

/** 页面恢复时只在本地确有未结束 durable execution 时建立执行连接。 */
export function hasPersistedDurableExecution(
  setting: ExecutionSetting,
  runtime: ExecutionRuntimeConfig = executionRuntime,
): boolean {
  return (
    readPersistedDurableExecution(getDurableExecutionStorageKey(runtime, setting), runtime) !== null
  );
}

/** 尽力写入或清除 durable attachment；存储不可用不应阻断聊天。 */
function writePersistedDurableExecution(
  key: string | undefined,
  value: PersistedDurableExecution | null,
  runtime: ExecutionRuntimeConfig,
): void {
  if (!key) return;
  try {
    const store = getAttachmentStore(runtime);
    if (value) store?.setItem(key, JSON.stringify(value));
    else store?.removeItem(key);
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

export class ExecutionSession {
  private readonly connection: HubConnection;
  /** 生成持久化隔离键所需的当前服务端配置。 */
  private readonly runtime: ExecutionRuntimeConfig;
  private handlers: ExecutionHubHandlers;
  private disposed = false;
  private hasActiveTurn = false;
  /** 当前用户轮次的稳定渲染作用域，确保实时 Tool Call/Result 可跨 UI 重附着配对。 */
  private activeStreamingScopeId: string | null = null;
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
  /** 当前自动重连状态，用于通知 UI 每一次重试计划。 */
  private reconnectState: ExecutionReconnectState | null = null;
  /** 标记 SignalR 已进入自动重连生命周期。 */
  private reconnecting = false;
  /** 当前自动重连等待的截止时间，用于避免重复执行已经开始的尝试。 */
  private automaticReconnectDeadlineMs: number | null = null;
  /** 标记手动重试已接管 SignalR 的当前自动重连轮次。 */
  private manualReconnectActive = false;
  /** 当前手动接管的重连任务，重复点击只会跳过下一段等待。 */
  private manualReconnectPromise: Promise<void> | null = null;
  /** 允许手动 Retry 提前结束当前重连等待。 */
  private resumeReconnectDelay: (() => void) | null = null;
  /** 让业务命令等待重连和执行上下文恢复完成。 */
  private reconnectCompletion: {
    promise: Promise<Error | null>;
    resolve: (error: Error | null) => void;
  } | null = null;

  public constructor(
    handlers: ExecutionHubHandlers,
    runtime: ExecutionRuntimeConfig = executionRuntime,
  ) {
    this.handlers = handlers;
    this.runtime = runtime;
    const hub = buildExecutionHubOptions(runtime);
    const reconnectPolicy: IRetryPolicy = {
      nextRetryDelayInMilliseconds: (context) => {
        const retryDelayMs = getExecutionReconnectDelay(context.previousRetryCount);
        this.automaticReconnectDeadlineMs =
          retryDelayMs === null ? null : Date.now() + retryDelayMs;
        if (retryDelayMs !== null) {
          this.updateReconnectState({
            status: "reconnecting",
            retryAttempt: context.previousRetryCount + 1,
            retryDelayMs,
          });
        }
        return retryDelayMs;
      },
    };
    this.connection = new HubConnectionBuilder()
      .withUrl(hub.url, hub.options)
      .configureLogging(LogLevel.Warning)
      .withAutomaticReconnect(reconnectPolicy)
      .build();

    this.connection.on("ReceiveMessage", (message: AiMessage) => {
      const scopedMessage = this.scopeIncomingMessage(message);
      this.updateDurableProgress(scopedMessage);
      if (scopedMessage.additionalProperties?.type === "turn-start") {
        this.hasActiveTurn = true;
      } else if (getTurnFinishedStatus(scopedMessage)) {
        this.finishActiveTurn();
      }

      if (!this.disposed) this.handlers.onMessage(scopedMessage);
    });
    this.connection.onreconnecting(() => {
      this.reconnecting = true;
      this.beginReconnect();
      this.updateReconnectState(
        this.reconnectState ?? {
          status: "reconnecting",
          retryAttempt: 1,
          retryDelayMs: 0,
        },
      );
    });
    this.connection.onclose((error) => {
      if (this.manualReconnectActive) return;

      this.automaticReconnectDeadlineMs = null;
      const reconnectExhausted =
        this.reconnecting && isExecutionReconnectExhausted(this.reconnectState);
      this.reconnecting = false;

      if (reconnectExhausted && !this.disposed) {
        this.failReconnect(new Error("Execution connection retries exhausted."));
        return;
      }

      if (this.durableConfirmed) this.hasActiveTurn = false;
      else this.finishActiveTurn();
      this.reconnectState = null;
      this.finishReconnect(error ?? new Error("Execution connection closed."));
      if (!this.disposed) this.handlers.onClose?.(error);
    });
    this.connection.onreconnected(() => {
      void this.completeReconnectAfterRestore();
    });
  }

  public setHandlers(handlers: ExecutionHubHandlers): void {
    this.handlers = handlers;
  }

  public async configure(setting: ExecutionSetting): Promise<ExecutionConfigurationResult> {
    this.setting = setting;
    this.durableStorageKey = getDurableExecutionStorageKey(this.runtime, setting);
    const persisted = readPersistedDurableExecution(this.durableStorageKey, this.runtime);
    if (persisted) {
      this.activeExecutionId = persisted.executionId;
      this.streamCursor = persisted.cursor;
      this.durableConfirmed = true;
      this.hasActiveTurn = true;
    }
    try {
      await this.ensureConnected();
      await this.refreshExecutionProvider();
      await this.dispatch(buildSettingCommand(setting));
      if (persisted) {
        if (this.executionProvider === "in-process") {
          this.finishActiveTurn();
        } else {
          const restoredDurableExecution = await this.restoreDurableSubscription(
            persisted.executionId,
            persisted.cursor,
          );
          return { restoredDurableExecution };
        }
      }
    } catch (error) {
      const normalized = error instanceof Error ? error : new Error(String(error));
      if (persisted) this.failReconnect(normalized);
      throw normalized;
    }

    return { restoredDurableExecution: false };
  }

  public hasActiveExecution(): boolean {
    return this.hasActiveTurn || (this.durableConfirmed && this.activeExecutionId !== null);
  }

  public async execute(request: ExecutionRequest): Promise<void> {
    const executionId = request.executionId ?? globalThis.crypto.randomUUID();
    this.activeExecutionId = executionId;
    this.activeStreamingScopeId = request.input.messageId;
    this.streamCursor = null;
    this.durableConfirmed = this.executionProvider === "distributed";
    this.hasActiveTurn = true;
    if (this.durableConfirmed) {
      writePersistedDurableExecution(
        this.durableStorageKey,
        {
          executionId,
          cursor: null,
        },
        this.runtime,
      );
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

  public async listAgentflowCheckpoints(
    agentflowId: string,
  ): Promise<AgentflowCheckpointAvailability[]> {
    try {
      await this.ensureConnected();
      return await this.connection.invoke<AgentflowCheckpointAvailability[]>(
        "GetAgentflowCheckpoints",
        agentflowId,
      );
    } catch (error) {
      const normalized = error instanceof Error ? error : new Error(String(error));
      this.handlers.onError?.(normalized);
      throw normalized;
    }
  }

  public async resumeCheckpoint(args: {
    checkpointOccurrenceId: string;
    agentflowId: string;
    resumeExecutionId?: string;
  }): Promise<string> {
    const resumeExecutionId = args.resumeExecutionId ?? globalThis.crypto.randomUUID();
    this.activeExecutionId = resumeExecutionId;
    this.streamCursor = null;
    this.durableConfirmed = this.executionProvider === "distributed";
    this.hasActiveTurn = true;
    if (this.durableConfirmed) {
      writePersistedDurableExecution(
        this.durableStorageKey,
        {
          executionId: resumeExecutionId,
          cursor: null,
        },
        this.runtime,
      );
    }

    try {
      await this.dispatch(
        buildResumeCheckpointCommand({
          checkpointOccurrenceId: args.checkpointOccurrenceId,
          resumeExecutionId,
          agentflowId: args.agentflowId,
        }),
      );
      return resumeExecutionId;
    } catch (error) {
      if (!this.durableConfirmed) {
        this.finishActiveTurn();
      } else if (this.connection.state === HubConnectionState.Connected) {
        try {
          if (await this.restoreDurableSubscription(resumeExecutionId, null)) {
            return resumeExecutionId;
          }
        } catch {
          // 与 execute 相同：无法确认服务端是否接受时保留 durable attachment。
        }
      }
      throw error;
    }
  }

  public async setMode(agentId: string, mode: AgentMode): Promise<void> {
    await this.dispatch(buildSetModeCommand(agentId, mode));
  }

  public async setPermissionMode(permissionMode: PermissionMode): Promise<void> {
    const previousSetting = this.setting;
    if (previousSetting) this.setting = { ...previousSetting, permissionMode };
    try {
      await this.dispatch(buildSetPermissionModeCommand(permissionMode));
    } catch (error) {
      this.setting = previousSetting;
      throw error;
    }
  }

  public async interrupt(reason?: string): Promise<void> {
    await this.dispatch(buildInterruptCommand(this.activeExecutionId ?? undefined, reason));
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
    await this.dispatch(
      buildHumanResponseCommand({
        ...(this.activeExecutionId ? { executionId: this.activeExecutionId } : {}),
        ...args,
      }),
    );
  }

  /** 立即执行当前重连次数；失败后继续等待下一次，耗尽后可重新开始一轮。 */
  public retryConnection(): Promise<void> {
    if (this.disposed) return Promise.reject(new Error("Execution connection is disposed"));

    if (this.manualReconnectPromise) {
      this.skipManualReconnectDelay();
      return this.manualReconnectPromise;
    }

    const state = this.reconnectState;
    if (!state) return Promise.resolve();
    if (
      state.status === "reconnecting" &&
      (state.retryDelayMs === 0 ||
        this.automaticReconnectDeadlineMs === null ||
        Date.now() >= this.automaticReconnectDeadlineMs)
    ) {
      return Promise.resolve();
    }

    const retryAttempt = state.status === "failed" ? 1 : state.retryAttempt;
    const cancelAutomaticReconnect = state.status === "reconnecting";
    const manualReconnectPromise = this.runManualReconnect(retryAttempt, cancelAutomaticReconnect);
    this.manualReconnectPromise = manualReconnectPromise;
    const clearManualReconnectPromise = () => {
      if (this.manualReconnectPromise === manualReconnectPromise) {
        this.manualReconnectPromise = null;
      }
    };
    void manualReconnectPromise.then(clearManualReconnectPromise, clearManualReconnectPromise);
    return manualReconnectPromise;
  }

  public async dispose(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    this.automaticReconnectDeadlineMs = null;
    this.resumeReconnectDelay?.();
    this.finishReconnect(new Error("Execution connection is disposed"));
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
    if (this.reconnectCompletion) {
      await this.waitForReconnect();
      return;
    }
    if (this.connection.state === HubConnectionState.Connected) return;
    if (this.connection.state === HubConnectionState.Reconnecting) {
      this.beginReconnect();
      await this.waitForReconnect();
      return;
    }
    if (this.connection.state !== HubConnectionState.Disconnected) {
      throw new Error(`Execution connection is ${this.connection.state}`);
    }
    await this.connection.start();
  }

  /** 创建一个可被业务命令等待的重连完成信号。 */
  private beginReconnect(): void {
    if (this.reconnectCompletion) return;
    let resolve!: (error: Error | null) => void;
    const promise = new Promise<Error | null>((complete) => {
      resolve = complete;
    });
    this.reconnectCompletion = { promise, resolve };
  }

  /** 完成当前重连等待，并把恢复错误传递给等待中的业务命令。 */
  private finishReconnect(error: Error | null): void {
    const completion = this.reconnectCompletion;
    this.reconnectCompletion = null;
    completion?.resolve(error);
  }

  /** 等待 SignalR 重连及服务端执行上下文恢复完成。 */
  private async waitForReconnect(): Promise<void> {
    const completion = this.reconnectCompletion;
    if (!completion) return;
    const error = await completion.promise;
    if (error) throw error;
    if (this.connection.state !== HubConnectionState.Connected) {
      throw new Error(`Execution connection is ${this.connection.state}`);
    }
  }

  /** 更新重试计划，并在重连期间同步给当前 UI。 */
  private updateReconnectState(state: ExecutionReconnectState): void {
    this.reconnectState = state;
    if (this.reconnecting && !this.disposed) this.handlers.onReconnecting?.(state);
  }

  /** 接管当前自动重连，并从当前次数开始执行可跳过等待的剩余计划。 */
  private async runManualReconnect(
    initialRetryAttempt: number,
    cancelAutomaticReconnect: boolean,
  ): Promise<void> {
    this.manualReconnectActive = true;
    this.automaticReconnectDeadlineMs = null;
    this.reconnecting = true;
    this.beginReconnect();
    this.updateReconnectState({
      status: "reconnecting",
      retryAttempt: initialRetryAttempt,
      retryDelayMs: 0,
    });

    let retryAttempt = initialRetryAttempt;
    let retryError = new Error("Execution connection retry failed.");

    try {
      if (cancelAutomaticReconnect) {
        await this.connection.stop();
      }

      while (!this.disposed && retryAttempt <= executionReconnectDelaysMs.length) {
        if (retryAttempt > initialRetryAttempt) {
          const retryDelayMs = getExecutionReconnectDelay(retryAttempt - 1);
          if (retryDelayMs === null) break;
          this.updateReconnectState({ status: "reconnecting", retryAttempt, retryDelayMs });
          await this.waitForManualReconnectDelay(retryDelayMs);
          if (this.disposed) return;
          this.updateReconnectState({ status: "reconnecting", retryAttempt, retryDelayMs: 0 });
        }

        try {
          if (this.connection.state === HubConnectionState.Disconnected) {
            await this.connection.start();
          } else if (this.connection.state !== HubConnectionState.Connected) {
            throw new Error(`Execution connection is ${this.connection.state}`);
          }
          await this.restoreAfterReconnect();
          this.finishReconnectSuccessfully();
          return;
        } catch (error) {
          retryError = error instanceof Error ? error : new Error(String(error));
          if (this.disposed) return;
          if (this.connection.state !== HubConnectionState.Disconnected) {
            await this.connection.stop();
          }
          retryAttempt += 1;
        }
      }

      if (!this.disposed) this.failReconnect(retryError);
    } catch (error) {
      const normalized = error instanceof Error ? error : new Error(String(error));
      if (!this.disposed) this.failReconnect(normalized);
    } finally {
      this.resumeReconnectDelay = null;
      this.manualReconnectActive = false;
    }
  }

  /** 等待下一次重连；Retry 可解析此 Promise 来立即消费该次尝试。 */
  private async waitForManualReconnectDelay(retryDelayMs: number): Promise<void> {
    await new Promise<void>((resolve) => {
      let timeoutId: ReturnType<typeof setTimeout> | undefined;
      const resume = () => {
        if (timeoutId !== undefined) clearTimeout(timeoutId);
        if (this.resumeReconnectDelay === resume) this.resumeReconnectDelay = null;
        resolve();
      };
      this.resumeReconnectDelay = resume;
      timeoutId = setTimeout(resume, retryDelayMs);
    });
  }

  /** 跳过手动接管后的当前等待，并同步 UI 为立即尝试。 */
  private skipManualReconnectDelay(): void {
    const state = this.reconnectState;
    if (!this.resumeReconnectDelay || state?.status !== "reconnecting") return;
    this.updateReconnectState({ ...state, retryDelayMs: 0 });
    this.resumeReconnectDelay();
  }

  /** 重连成功后先恢复设置和 durable 订阅，再解除 Chat 阻塞。 */
  private async completeReconnectAfterRestore(): Promise<void> {
    try {
      await this.restoreAfterReconnect();
      this.finishReconnectSuccessfully();
    } catch (error) {
      const normalized = error instanceof Error ? error : new Error(String(error));
      this.reconnecting = false;
      this.reconnectState = null;
      this.finishReconnect(normalized);
      if (!this.disposed) this.handlers.onError?.(normalized);
      await this.connection.stop();
    }
  }

  /** 清理重连状态，并在 execution 上下文恢复后解除 Chat 阻塞。 */
  private finishReconnectSuccessfully(): void {
    this.reconnecting = false;
    this.automaticReconnectDeadlineMs = null;
    this.reconnectState = null;
    this.finishReconnect(null);
    if (!this.disposed) this.handlers.onReconnected?.();
  }

  /** 保留可手动重试的失败状态，并结束当前重连等待。 */
  private failReconnect(error: Error): void {
    this.reconnecting = false;
    this.automaticReconnectDeadlineMs = null;
    if (this.durableConfirmed) this.hasActiveTurn = false;
    else this.finishActiveTurn();
    const failedState: ExecutionReconnectState = {
      status: "failed",
      retryAttempt: executionReconnectDelaysMs.length,
      retryDelayMs: 0,
    };
    this.reconnectState = failedState;
    this.finishReconnect(error);
    if (!this.disposed) this.handlers.onReconnectFailed?.(failedState);
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
      return this.hasActiveExecution();
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
    writePersistedDurableExecution(
      this.durableStorageKey,
      {
        executionId,
        cursor: this.streamCursor,
      },
      this.runtime,
    );
  }

  private scopeIncomingMessage(message: AiMessage): AiMessage {
    const explicitScopeId = getMessageStreamingScopeId(message);
    const scopeId = explicitScopeId ?? this.activeStreamingScopeId ?? message.messageId;
    if (explicitScopeId || message.additionalProperties?.type === "turn-start") {
      this.activeStreamingScopeId = scopeId;
    }
    return scopeStreamingMessage(message, scopeId);
  }

  private finishActiveTurn(): void {
    this.hasActiveTurn = false;
    this.activeExecutionId = null;
    this.activeStreamingScopeId = null;
    this.streamCursor = null;
    this.durableConfirmed = false;
    writePersistedDurableExecution(this.durableStorageKey, null, this.runtime);
    for (const resolve of this.turnFinishedWaiters) {
      resolve();
    }
    this.turnFinishedWaiters.clear();
  }
}

export { ExecutionSession as ExecutionHubClient };
