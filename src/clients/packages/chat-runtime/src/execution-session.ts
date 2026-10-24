import type { HumanResponseCommandInput } from "@agw/execution-core";
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
  buildSubscribeTurnCommand as buildCoreSubscribeTurnCommand,
  DEFAULT_AGENT_MODE,
  executionReconnectDelaysMs,
  executionSilentReconnectAttempts,
  getAgentMode,
  getExecutionReconnectDelay,
  getLatestAgentMode,
  getMessageStreamingScopeId,
  getTurnFinishedStatus,
  getTurnPosition,
  isModeControlMessage,
  isTurnStartMessage,
  isUserTurnMessage,
  scopeStreamingMessage,
  type AgentMode,
  type ExecutionHubMethod,
  type ExecutionUserInput as CoreExecutionUserInput,
  type PermissionMode as CorePermissionMode,
  type TurnFinishedStatus,
  type TurnPosition,
} from "@agw/execution-core";
import {
  getAgentflowCheckpointMessage,
  getPendingInteraction,
  type AgentflowCheckpointAvailability,
  type PendingInteraction,
} from "@agw/chat-core";

export { getAgentflowCheckpointMessage, getPendingInteraction } from "@agw/chat-core";
export type {
  AgentflowCheckpointAvailability,
  AgentflowCheckpointMarkerInfo,
  AgentflowCheckpointMessage,
  PendingInteraction,
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

/** 前五次重连静默；对用户仅呈现后五次的进度，内部计数保持不变。 */
export function getExecutionReconnectProgress(
  state: ExecutionReconnectState | null,
): { attempt: number; total: number } | null {
  if (
    !state ||
    (state.status === "reconnecting" && state.retryAttempt <= executionSilentReconnectAttempts)
  ) {
    return null;
  }
  const total = executionReconnectDelaysMs.length - executionSilentReconnectAttempts;
  return {
    attempt:
      state.status === "failed" ? total : state.retryAttempt - executionSilentReconnectAttempts,
    total,
  };
}

/** 判断 SignalR 是否已经完成最后一次自动重连尝试。 */
export function isExecutionReconnectExhausted(state: ExecutionReconnectState | null): boolean {
  return (
    state?.status === "reconnecting" && state.retryAttempt === executionReconnectDelaysMs.length
  );
}

export function configureExecutionRuntime(config: ExecutionRuntimeConfig): void {
  executionRuntime = config;
}

/** configureExecutionRuntime 的当前配置。The current configuration of configureExecutionRuntime. */
export function getExecutionRuntime(): ExecutionRuntimeConfig {
  return executionRuntime;
}

/** ExecutionSession 向 ChatHttpClient 报告通信。How ExecutionSession reports traffic to ChatHttpClient. */
export type ExecutionTrafficObserver = {
  /**
   * hub 调用发出前调用；返回的函数在调用结束时接收结果。
   * Called before a hub call; the returned function receives its outcome.
   */
  request(
    method: ExecutionHubMethod,
    args: readonly unknown[],
  ): (outcome: { ok: true; value: unknown } | { ok: false; error: unknown }) => void;
  /** handlers.onMessage 处理一条 ReceiveMessage 之后调用。Called after handlers.onMessage handles a ReceiveMessage. */
  message(message: AiMessage): void;
};

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
  /** 连接所属的对话；草稿对话同样使用它，首次受理时服务端才保存对话。 */
  conversationId: string;
  environmentVariables?: Record<string, string> | null;
  permissionMode?: PermissionMode;
  resultOnly?: boolean;
};

/**
 * 服务端明确拒绝启动，或确认该 turnId 不存在；这次发送没有执行。
 * The server explicitly refused the start, or confirmed the turnId does not exist; this send did not run.
 */
export class ExecutionStartRejectedError extends Error {
  public constructor(cause: unknown) {
    super(cause instanceof Error ? cause.message : String(cause), { cause });
    this.name = "ExecutionStartRejectedError";
  }
}

export type ExecutionConfigurationResult = {
  restoredDurableExecution: boolean;
};

export type ExecutionTarget = {
  agentId: string;
  agentType: number;
};

export type ExecutionRequest = ExecutionTarget & {
  /** 客户端生成的 Project Conversation 标识。 */
  conversationId: string;
  /** 客户端生成的稳定执行标识，用于 durable 启动幂等和断线恢复。 */
  turnId?: string;
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
export function buildSubscribeTurnCommand(turnId: string, cursor?: string | null) {
  return buildCoreSubscribeTurnCommand(turnId, cursor);
}

/** 读取服务端持久化的作用域；委托给 platform-neutral 的 execution-core，保持单一来源。 */
export { getMessageStreamingScopeId };

const executionInterruptTimeoutMs = 3_000;

/** 浏览器为当前服务端、项目和对话保存的 durable attachment。 */
type PersistedDurableExecution = {
  /** 尚未确认结束的业务执行标识。 */
  turnId: string;
  /** 客户端已经收到的最大 turnSequence。 */
  cursor: string | null;
};

/** 服务端声明的执行恢复能力；null 表示旧服务端未提供能力接口。 */
type ExecutionProviderCapability = "in-process" | "distributed" | null;

/** 重新订阅 durable execution 的结果：仍在运行、已经结束，或服务端确认不存在。 */
type DurableRestoreResult = "active" | "finished" | "missing";

/** 服务端以 AgwException 拒绝的命令在 SignalR 错误中带有 HubException 标记。 */
function isServerRejection(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error);
  return message.includes("HubException:");
}

function getAttachmentServer(runtime: ExecutionRuntimeConfig): string {
  return runtime.baseUrl.replace(/\/+$/u, "") || "local";
}

/** 为一个服务端上的项目对话生成互不冲突的 durable attachment 存储键。 */
export function getDurableExecutionStorageKey(
  runtime: ExecutionRuntimeConfig,
  setting: Pick<ExecutionSetting, "projectId" | "conversationId">,
): string {
  return `agw:durable-execution:v2:${JSON.stringify([
    getAttachmentServer(runtime),
    setting.projectId,
    setting.conversationId,
  ])}`;
}

/** 按执行上下文保存的旧 durable attachment 存储键，只用于迁移。 */
function getContextDurableExecutionStorageKey(
  runtime: ExecutionRuntimeConfig,
  projectId: string,
  contextId: string,
): string {
  return `agw:durable-execution:v1:${JSON.stringify([
    getAttachmentServer(runtime),
    projectId,
    contextId,
  ])}`;
}

/**
 * 把按执行上下文保存的 durable attachment 移到按对话保存的键下，保留 turnId 与 cursor；新键写入成功后删除旧记录。
 * 必须在该对话的恢复配置之前调用，否则这次恢复读不到记录。
 * Moves a durable attachment saved by execution context to the conversation key, keeping turnId and cursor; the old record is removed after the new key is written.
 * It must run before the conversation's restore configuration, or that restore cannot find the record.
 */
export function migrateDurableExecutionAttachment(
  conversation: { projectId: string; conversationId: string; contextId: string },
  runtime: ExecutionRuntimeConfig = executionRuntime,
): void {
  const store = getAttachmentStore(runtime);
  if (!store) return;
  const contextKey = getContextDurableExecutionStorageKey(
    runtime,
    conversation.projectId,
    conversation.contextId,
  );
  const persisted = readPersistedDurableExecution(contextKey, runtime);
  if (!persisted) return;
  const conversationKey = getDurableExecutionStorageKey(runtime, conversation);
  // 新键已有记录时它更新，只删除旧记录。A record already under the new key is newer; only the old one is removed.
  if (readPersistedDurableExecution(conversationKey, runtime) === null) {
    store.setItem(conversationKey, JSON.stringify(persisted));
  }
  store.removeItem(contextKey);
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
    return typeof parsed.turnId === "string" && parsed.turnId.trim().length > 0
      ? {
          turnId: parsed.turnId,
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
  /** 原始进程内执行所属连接；连续重连不能覆盖它。 */
  private executionConnectionId: string | null = null;
  private recoveringInProcess = false;
  private recoveryTimer: ReturnType<typeof setTimeout> | null = null;
  private recoveryRevision = 0;
  /** 当前用户轮次的稳定渲染作用域，确保实时 Tool Call/Result 可跨 UI 重附着配对。 */
  private activeStreamingScopeId: string | null = null;
  private readonly turnFinishedWaiters = new Set<() => void>();
  /** 重连后必须先恢复的服务端执行设置。 */
  private setting: ExecutionSetting | null = null;
  /** 当前项目会话对应的浏览器存储键。 */
  private durableStorageKey: string | undefined;
  /** 当前启动或附着的 durable execution。 */
  private activeTurnId: string | null = null;
  /**
   * 当前 Turn 最后连续处理成功的位置，与 attachment store 中的 cursor 相同。
   * The last position of the current turn processed successfully without gaps, the same as the cursor in the attachment store.
   */
  private streamCursor: string | null = null;
  /**
   * 正在处理或处理失败、尚未确认的 Turn 位置；存在时只重新处理这个位置的消息。
   * The turn position being processed, or failed and not confirmed; while it exists only that position is processed again.
   */
  private pendingPosition: TurnPosition | null = null;
  private readonly observer: ExecutionTrafficObserver | null;
  private readonly extensionId: string | null;
  /** 标记 turnId 已被 durable 服务端确认，断线时不得误判执行结束。 */
  private durableConfirmed = false;
  /** 服务端选择的执行提供程序能力。 */
  private executionProvider: ExecutionProviderCapability = null;
  /** 当前自动重连状态，用于通知 UI 每一次重试计划。 */
  private reconnectState: ExecutionReconnectState | null = null;
  /** 初始化和 checkpoint 查询共享同一次连接建立。 */
  private connectionStart: Promise<void> | null = null;
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

  /**
   * @param extensionId 会话代表的 Desktop extension；每个 SettingCommand 都带上它，宿主的会话为 null。
   * The Desktop extension the session represents; every SettingCommand carries it, and host sessions pass null.
   */
  public constructor(
    handlers: ExecutionHubHandlers,
    runtime: ExecutionRuntimeConfig = executionRuntime,
    observer: ExecutionTrafficObserver | null = null,
    extensionId: string | null = null,
  ) {
    this.handlers = handlers;
    this.runtime = runtime;
    this.observer = observer;
    this.extensionId = extensionId;
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

    this.connection.on("ReceiveMessage", (message: AiMessage) => this.receiveMessage(message));
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

      if (!this.disposed && this.hasRunningTurn()) {
        this.failReconnect(error ?? new Error("Execution connection closed."));
        return;
      }
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
      this.activeTurnId = persisted.turnId;
      this.streamCursor = persisted.cursor;
      this.durableConfirmed = true;
      this.hasActiveTurn = true;
    }
    try {
      await this.ensureConnected();
      await this.refreshExecutionProvider();
      await this.dispatch(this.createSettingCommand(setting));
      if (persisted) {
        if (this.executionProvider === "in-process") {
          this.finishActiveTurn();
        } else {
          const restored = await this.restoreDurableSubscription(
            persisted.turnId,
            persisted.cursor,
          );
          return { restoredDurableExecution: restored === "active" };
        }
      }
      if (this.executionProvider === "in-process") await this.discoverInProcessExecution();
    } catch (error) {
      const normalized = error instanceof Error ? error : new Error(String(error));
      this.failReconnect(normalized);
      throw normalized;
    }

    return { restoredDurableExecution: false };
  }

  public hasRunningTurn(): boolean {
    return this.hasActiveTurn || (this.durableConfirmed && this.activeTurnId !== null);
  }

  public async execute(request: ExecutionRequest): Promise<void> {
    if (this.hasRunningTurn()) throw new Error("This conversation already has a running task.");
    const turnId = request.turnId ?? globalThis.crypto.randomUUID();
    this.activeTurnId = turnId;
    this.activeStreamingScopeId = turnId;
    this.streamCursor = null;
    this.durableConfirmed = this.executionProvider === "distributed";
    this.hasActiveTurn = true;
    if (this.durableConfirmed) {
      writePersistedDurableExecution(
        this.durableStorageKey,
        {
          turnId,
          cursor: null,
        },
        this.runtime,
      );
    }
    try {
      await this.ensureConnected();
      this.executionConnectionId = this.connection.connectionId;
      await this.dispatch(buildExecCommand({ ...request, turnId }));
    } catch (error) {
      if (await this.reconcileStartFailure(turnId, error)) return;
      throw error;
    }
  }

  public async listAgentflowCheckpoints(
    agentflowId: string,
  ): Promise<AgentflowCheckpointAvailability[]> {
    try {
      await this.ensureConnected();
      return await this.invoke<AgentflowCheckpointAvailability[]>(
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
    resumeTurnId?: string;
  }): Promise<string> {
    if (this.hasRunningTurn()) throw new Error("This conversation already has a running task.");
    const resumeTurnId = args.resumeTurnId ?? globalThis.crypto.randomUUID();
    this.activeTurnId = resumeTurnId;
    this.streamCursor = null;
    this.durableConfirmed = this.executionProvider === "distributed";
    this.hasActiveTurn = true;
    if (this.durableConfirmed) {
      writePersistedDurableExecution(
        this.durableStorageKey,
        {
          turnId: resumeTurnId,
          cursor: null,
        },
        this.runtime,
      );
    }

    try {
      await this.ensureConnected();
      this.executionConnectionId = this.connection.connectionId;
      await this.dispatch(
        buildResumeCheckpointCommand({
          checkpointOccurrenceId: args.checkpointOccurrenceId,
          resumeTurnId,
          agentflowId: args.agentflowId,
        }),
      );
      return resumeTurnId;
    } catch (error) {
      if (await this.reconcileStartFailure(resumeTurnId, error)) return resumeTurnId;
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
    if (this.recoveringInProcess) {
      await this.ensureConnected();
      await this.recoverInProcessExecution(true);
      return;
    }
    await this.dispatch(buildInterruptCommand(this.activeTurnId ?? undefined, reason));
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

  public async submitHumanResponse(args: HumanResponseCommandInput): Promise<void> {
    await this.dispatch(
      buildHumanResponseCommand({
        turnId: args.turnId ?? this.activeTurnId ?? undefined,
        response: args.response,
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
    this.recoveryRevision++;
    this.clearRecoveryTimer();
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
      await this.invoke("DispatchCommand", command);
    } catch (error) {
      const normalized = error instanceof Error ? error : new Error(String(error));
      this.handlers.onError?.(normalized);
      throw normalized;
    }
  }

  /**
   * 调用 hub 方法，并在发出前、结束后报告给观察者；观察者抛出的异常让这次调用失败。
   * Invokes a hub method, reporting it to the observer before sending and after it ends; an exception the observer throws fails the call.
   */
  private async invoke<T>(method: ExecutionHubMethod, ...args: unknown[]): Promise<T> {
    const complete = this.observer?.request(method, args);
    let value: T;
    try {
      value = await this.connection.invoke<T>(method, ...args);
    } catch (error) {
      complete?.({ ok: false, error });
      throw error;
    }
    complete?.({ ok: true, value });
    return value;
  }

  private async ensureConnected(): Promise<void> {
    if (this.disposed) throw new Error("Execution connection is disposed");
    if (this.reconnectCompletion) {
      await this.waitForReconnect();
      return;
    }
    if (this.connection.state === HubConnectionState.Connected) return;
    if (this.connectionStart) {
      await this.connectionStart;
      return;
    }
    if (this.connection.state === HubConnectionState.Reconnecting) {
      this.beginReconnect();
      await this.waitForReconnect();
      return;
    }
    if (this.connection.state !== HubConnectionState.Disconnected) {
      throw new Error(`Execution connection is ${this.connection.state}`);
    }
    const start = this.connection.start();
    this.connectionStart = start;
    try {
      await start;
    } finally {
      if (this.connectionStart === start) this.connectionStart = null;
    }
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
      if (!this.disposed) this.failReconnect(normalized);
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
    this.clearRecoveryTimer();
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
    await this.invoke("DispatchCommand", this.createSettingCommand(this.setting));
    if (!this.durableConfirmed && this.hasRunningTurn()) {
      this.recoveringInProcess = true;
      // skipNegotiation 的 WebSocket 连接没有客户端 connectionId，按会话发现原执行。
      if (this.executionConnectionId) await this.recoverInProcessExecution();
      else await this.discoverInProcessExecution();
    } else if (this.durableConfirmed && this.activeTurnId) {
      await this.restoreDurableSubscription(this.activeTurnId, this.streamCursor);
    } else if (this.executionProvider === "in-process") {
      await this.discoverInProcessExecution();
    }
  }

  /** 重新加载页面后不再有旧连接 ID，必须从服务端发现原执行。 */
  private async discoverInProcessExecution(): Promise<void> {
    if (!this.setting?.projectId || !this.setting.conversationId) return;
    const connectionId = await this.invoke<string | null>(
      "FindInProcessExecution",
      this.setting.projectId,
      this.setting.conversationId,
    );
    if (this.disposed) return;
    if (connectionId === null) {
      if (this.recoveringInProcess) this.finishActiveTurn();
      return;
    }
    if (typeof connectionId !== "string" || !connectionId)
      throw new Error("Cannot confirm the previous execution's state.");
    this.executionConnectionId = connectionId;
    this.hasActiveTurn = true;
    this.recoveringInProcess = true;
    await this.recoverInProcessExecution();
  }

  /**
   * 启动命令失败后确认结果：执行已附着并仍在运行时返回 true；确认没有执行时抛出 ExecutionStartRejectedError；无法确认时返回 false 并保留状态供恢复。
   * Confirms the outcome after a failed start command: returns true when the execution is attached and still running; throws ExecutionStartRejectedError when it is confirmed not to have run; returns false and keeps state for recovery when it cannot be confirmed.
   */
  private async reconcileStartFailure(turnId: string, error: unknown): Promise<boolean> {
    if (!this.durableConfirmed) {
      await this.reconcileInProcessStartFailure(error);
      if (isServerRejection(error)) throw new ExecutionStartRejectedError(error);
      return false;
    }
    if (this.connection.state !== HubConnectionState.Connected) return false;
    let restored: DurableRestoreResult;
    try {
      restored = await this.restoreDurableSubscription(turnId, null);
    } catch {
      // 短暂探测失败不能证明服务端未接受启动，因此保留 turnId 供后续恢复。
      return false;
    }
    if (restored === "missing") throw new ExecutionStartRejectedError(error);
    return restored === "active";
  }

  /** 发送失败不代表启动失败：只有明确拒绝或服务端确认空闲才能清理执行。 */
  private async reconcileInProcessStartFailure(error: unknown): Promise<void> {
    if (isServerRejection(error)) {
      this.finishActiveTurn();
      return;
    }
    this.recoveringInProcess = true;
    if (this.connection.state === HubConnectionState.Connected) {
      try {
        if (this.executionConnectionId) await this.recoverInProcessExecution();
        else await this.discoverInProcessExecution();
      } catch (recoveryError) {
        this.failReconnect(
          recoveryError instanceof Error ? recoveryError : new Error(String(recoveryError)),
        );
      }
    }
  }

  /** 旧连接仍可继续执行；确认其空闲前保留 busy，并把停止操作路由给原连接。 */
  private async recoverInProcessExecution(interrupt = false): Promise<void> {
    const connectionId = this.executionConnectionId;
    const turnId = this.activeTurnId;
    if (!connectionId) throw new Error("Cannot confirm the previous execution's state.");
    this.clearRecoveryTimer();
    const revision = ++this.recoveryRevision;
    let active: boolean;
    try {
      active = await this.invoke<boolean>("RecoverInProcessExecution", connectionId, interrupt);
    } catch (error) {
      if (this.disposed || revision !== this.recoveryRevision || this.activeTurnId !== turnId)
        return;
      // 服务端拒绝停止（例如只读的 extension 会话）时执行仍在运行，继续查询直到它结束。
      // A stop the server refuses (such as from a read-only extension session) leaves the execution running, so polling continues until it ends.
      if (interrupt && isServerRejection(error)) this.scheduleRecoveryPoll();
      throw error;
    }
    if (this.disposed || revision !== this.recoveryRevision || this.activeTurnId !== turnId) return;
    if (typeof active !== "boolean")
      throw new Error("Cannot confirm the previous execution's state.");
    if (!active) {
      this.finishActiveTurn();
      if (!this.reconnecting) this.handlers.onReconnected?.();
      return;
    }
    this.scheduleRecoveryPoll();
  }

  /** 一秒后再次查询旧连接的执行状态。Queries the old connection's execution state again after one second. */
  private scheduleRecoveryPoll(): void {
    this.recoveryTimer = setTimeout(() => {
      this.recoveryTimer = null;
      if (
        this.disposed ||
        this.connection.state !== HubConnectionState.Connected ||
        this.reconnecting
      )
        return;
      void this.recoverInProcessExecution().catch((error: unknown) => {
        if (!this.disposed)
          this.failReconnect(error instanceof Error ? error : new Error(String(error)));
      });
    }, 1000);
  }

  /**
   * 本会话的 SettingCommand：extension 的会话带上它的身份，宿主的会话从不带；设置中多传的 extensionId 被覆盖。
   * This session's SettingCommand: an extension session carries its identity and a host session never does; an extensionId passed in the settings is overridden.
   */
  private createSettingCommand(setting: ExecutionSetting) {
    return buildCoreSettingCommand({ ...setting, extensionId: this.extensionId ?? undefined });
  }

  private clearRecoveryTimer(): void {
    if (this.recoveryTimer !== null) clearTimeout(this.recoveryTimer);
    this.recoveryTimer = null;
  }

  /** 尝试重新订阅指定执行；只有明确不存在时才清理本地 attachment。 */
  private async restoreDurableSubscription(
    turnId: string,
    cursor: string | null,
  ): Promise<DurableRestoreResult> {
    try {
      await this.invoke("DispatchCommand", buildSubscribeTurnCommand(turnId, cursor));
      return this.hasRunningTurn() ? "active" : "finished";
    } catch (error) {
      const normalized = error instanceof Error ? error : new Error(String(error));
      // 服务端 HubException 的消息以七位错误码开头：4040011 为 DurableExecutionNotFound。
      // A server HubException message starts with the seven-digit code: 4040011 is DurableExecutionNotFound.
      if (normalized.message.includes("HubException: 4040011:")) {
        this.finishActiveTurn();
        return "missing";
      }
      throw normalized;
    }
  }

  /** 读取当前服务端的执行提供程序；失败交由连接错误流程处理。 */
  private async refreshExecutionProvider(): Promise<void> {
    const provider = await this.invoke<string>("GetExecutionProvider");
    switch (provider.toLowerCase()) {
      case "distributed":
        this.executionProvider = "distributed";
        break;
      case "inprocess":
        this.executionProvider = "in-process";
        break;
      default:
        throw new Error("The server returned an unsupported execution provider.");
    }
  }

  /**
   * 处理一条推送。Distributed 模式下带有 Turn 位置的消息在处理函数与观察者都正常返回后才确认：之前 cursor 不前进，失败时位置保留；
   * 存在未确认的位置时只重新处理这个位置，其他带有位置的消息等待恢复订阅后由服务端补发。结束消息确认之后才结束 Turn。
   * Handles one pushed message. In Distributed mode a message with a turn position is confirmed only after the handlers and the observer both return: the cursor does not move before that, and a failure keeps the position;
   * while a position is unconfirmed only that position is processed again, and other positioned messages wait for the server to replay them after resubscribing. A finish message ends the turn only once confirmed.
   */
  private receiveMessage(message: AiMessage): void {
    const position = this.executionProvider === "distributed" ? getTurnPosition(message) : null;
    if (position) {
      if (this.pendingPosition && !isSamePosition(this.pendingPosition, position)) return;
      this.beginDurablePosition(position);
    }
    const scopedMessage = this.scopeIncomingMessage(message);
    const finished = getTurnFinishedStatus(scopedMessage) !== null;
    if (isTurnStartMessage(scopedMessage)) {
      this.hasActiveTurn = true;
    } else if (finished && !position && !this.pendingPosition) {
      // 没有 Turn 位置的结束消息不能清除等待恢复的 Turn。A finish message without a turn position cannot clear a turn awaiting recovery.
      this.finishActiveTurn();
    }

    if (!this.disposed) {
      this.handlers.onMessage(scopedMessage);
      this.observer?.message(message);
    }
    if (!position) return;
    this.pendingPosition = null;
    this.confirmDurablePosition(position);
    if (finished) this.finishActiveTurn();
  }

  /**
   * 开始处理一个 Turn 位置：属于另一个 Turn 时先建立它，cursor 为空；再记为待确认的位置。
   * Starts processing a turn position: one of another turn establishes that turn first with an empty cursor, then it becomes the pending position.
   */
  private beginDurablePosition(position: TurnPosition): void {
    if (this.activeTurnId !== position.turnId) {
      this.activeTurnId = position.turnId;
      this.streamCursor = null;
      writePersistedDurableExecution(
        this.durableStorageKey,
        { turnId: position.turnId, cursor: null },
        this.runtime,
      );
    }
    this.durableConfirmed = true;
    this.pendingPosition = position;
  }

  /**
   * 处理函数与观察者都已正常返回：推进内存中的 cursor 并写入 attachment store。
   * The handlers and the observer both returned: advances the in-memory cursor and writes the attachment store.
   */
  private confirmDurablePosition(position: TurnPosition): void {
    const confirmed = this.streamCursor === null ? 0 : Number(this.streamCursor);
    if (position.turnSequence > confirmed) this.streamCursor = String(position.turnSequence);
    writePersistedDurableExecution(
      this.durableStorageKey,
      { turnId: position.turnId, cursor: this.streamCursor },
      this.runtime,
    );
  }

  private scopeIncomingMessage(message: AiMessage): AiMessage {
    const explicitScopeId = getMessageStreamingScopeId(message);
    const scopeId = explicitScopeId ?? this.activeStreamingScopeId ?? message.messageId;
    if (explicitScopeId || isTurnStartMessage(message)) {
      this.activeStreamingScopeId = scopeId;
    }
    return scopeStreamingMessage(message, scopeId);
  }

  private finishActiveTurn(): void {
    this.recoveryRevision++;
    this.clearRecoveryTimer();
    this.executionConnectionId = null;
    this.recoveringInProcess = false;
    this.hasActiveTurn = false;
    this.activeTurnId = null;
    this.activeStreamingScopeId = null;
    this.streamCursor = null;
    this.pendingPosition = null;
    this.durableConfirmed = false;
    writePersistedDurableExecution(this.durableStorageKey, null, this.runtime);
    for (const resolve of this.turnFinishedWaiters) {
      resolve();
    }
    this.turnFinishedWaiters.clear();
  }
}

function isSamePosition(left: TurnPosition, right: TurnPosition): boolean {
  return left.turnId === right.turnId && left.turnSequence === right.turnSequence;
}

export { ExecutionSession as ExecutionHubClient };

export type {
  InteractionResponse,
  ApprovalScope,
  HumanResponseCommandInput,
} from "@agw/execution-core";
