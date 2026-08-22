import type { AiMessage } from "@agw/api";
import {
  HubConnectionBuilder,
  HubConnectionState,
  HttpTransportType,
  LogLevel,
  type HubConnection,
  type IHttpConnectionOptions,
  type IRetryPolicy,
} from "@microsoft/signalr";
import {
  buildExecCommand,
  buildInterruptCommand,
  buildSetModeCommand,
  buildSetPermissionModeCommand,
  buildSettingCommand,
  buildSubscribeExecutionCommand,
  getExecutionReconnectDelay,
  getTurnFinishedStatus,
  type AgentMode,
  type ExecutionUserInput,
  type PermissionMode,
} from "@agw/execution-core";

export type MobileExecutionSetting = {
  projectId: string;
  contextId: string;
  permissionMode: PermissionMode;
};

export type MobileExecutionRequest = {
  agentId: string;
  agentType: 0 | 1;
  executionId: string;
  input: ExecutionUserInput<AiMessage>;
};

export type ExecutionReconnectState = {
  retryAttempt: number;
  retryDelayMs: number;
};

export type MobileExecutionSessionOptions = {
  serverUrl: string;
  token: string;
  onMessage(message: AiMessage): void;
  onClose?(error: Error): void;
  onReconnecting?(state: ExecutionReconnectState | null): void;
};

type ProviderCapability = "distributed" | "in-process" | null;

type ActiveTurn = {
  executionId: string;
  durableConfirmed: boolean;
  streamCursor: string | null;
  promise: Promise<void>;
  resolve(): void;
  reject(error: Error): void;
};

const serverTimeoutMs = 30_000;

export function buildMobileSettingCommand(setting: MobileExecutionSetting) {
  return buildSettingCommand(setting);
}

export class MobileExecutionSession {
  private readonly connection: HubConnection;
  private readonly options: MobileExecutionSessionOptions;
  private setting: MobileExecutionSetting | null = null;
  private provider: ProviderCapability = null;
  private activeTurn: ActiveTurn | null = null;
  private disposed = false;
  private reconnecting = false;
  private reconnectState: ExecutionReconnectState | null = null;
  private startPromise: Promise<void> | null = null;
  private reconnectCompletion: {
    promise: Promise<Error | null>;
    resolve(error: Error | null): void;
  } | null = null;

  public constructor(options: MobileExecutionSessionOptions) {
    this.options = options;
    const hubUrl = `${options.serverUrl.replace(/\/+$/u, "")}/api/hubs/exec`;
    const reconnectPolicy: IRetryPolicy = {
      nextRetryDelayInMilliseconds: (context) => {
        const retryDelayMs = getExecutionReconnectDelay(context.previousRetryCount);
        if (retryDelayMs !== null) {
          this.reconnectState = {
            retryAttempt: context.previousRetryCount + 1,
            retryDelayMs,
          };
          if (this.reconnecting) this.options.onReconnecting?.(this.reconnectState);
        }
        return retryDelayMs;
      },
    };
    const connectionOptions: IHttpConnectionOptions & { WebSocket: typeof WebSocket } = {
      transport: HttpTransportType.WebSockets,
      skipNegotiation: true,
      accessTokenFactory: async () => options.token,
      WebSocket: globalThis.WebSocket,
    };
    this.connection = new HubConnectionBuilder()
      .withUrl(hubUrl, connectionOptions)
      .configureLogging(LogLevel.Warning)
      .withAutomaticReconnect(reconnectPolicy)
      .build();
    this.connection.serverTimeoutInMilliseconds = serverTimeoutMs;

    this.connection.on("ReceiveMessage", (message: AiMessage) => this.receiveMessage(message));
    this.connection.onreconnecting(() => {
      if (this.disposed) return;
      this.reconnecting = true;
      this.beginReconnect();
      this.reconnectState ??= {
        retryAttempt: 1,
        retryDelayMs: getExecutionReconnectDelay(0) ?? 0,
      };
      this.options.onReconnecting?.(this.reconnectState);
    });
    this.connection.onreconnected(() => {
      void this.restoreAfterReconnect()
        .then(() => this.finishReconnectSuccessfully())
        .catch((error) => this.failReconnect(toError(error)));
    });
    this.connection.onclose((error) => {
      if (this.disposed) return;
      const normalized = error ?? new Error("Execution connection retries were exhausted.");
      this.reconnecting = false;
      this.reconnectState = null;
      this.finishReconnect(normalized);
      this.options.onReconnecting?.(null);
      this.finishActiveTurn(normalized);
      this.options.onClose?.(normalized);
    });
  }

  public async configure(setting: MobileExecutionSetting): Promise<void> {
    this.setting = setting;
    await this.ensureConnected();
    if (this.disposed) throw new Error("Execution session is disposed.");
    await this.refreshProvider();
    if (this.disposed) throw new Error("Execution session is disposed.");
    await this.connection.invoke("DispatchCommand", buildMobileSettingCommand(setting));
  }

  public async execute(request: MobileExecutionRequest): Promise<void> {
    if (!this.setting) throw new Error("Execution session is not configured.");
    if (this.activeTurn) throw new Error("This conversation already has a running task.");

    const turn = createActiveTurn(request.executionId, this.provider === "distributed");
    this.activeTurn = turn;
    try {
      await this.dispatch(
        buildExecCommand({
          agentId: request.agentId,
          agentType: request.agentType,
          executionId: request.executionId,
          stream: true,
          input: request.input,
        }),
      );
    } catch (error) {
      const normalized = toError(error);
      if (turn.durableConfirmed && this.connection.state === HubConnectionState.Connected) {
        try {
          await this.subscribeActiveTurn(turn);
        } catch {
          this.finishActiveTurn(normalized);
        }
      } else {
        this.finishActiveTurn(normalized);
      }
    }

    return turn.promise;
  }

  public async setMode(agentId: string, mode: AgentMode): Promise<void> {
    await this.dispatch(buildSetModeCommand(agentId, mode));
  }

  public async setPermissionMode(permissionMode: PermissionMode): Promise<void> {
    if (!this.setting) throw new Error("Execution session is not configured.");
    const previousSetting = this.setting;
    this.setting = { ...this.setting, permissionMode };
    try {
      await this.dispatch(buildSetPermissionModeCommand(permissionMode));
    } catch (error) {
      this.setting = previousSetting;
      throw error;
    }
  }

  public async interrupt(reason?: string): Promise<void> {
    await this.dispatch(buildInterruptCommand(this.activeTurn?.executionId, reason));
  }

  public async dispose(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    const error = new Error("Execution session was disposed.");
    this.finishReconnect(error);
    this.finishActiveTurn(error);
    this.options.onReconnecting?.(null);
    if (this.connection.state !== HubConnectionState.Disconnected) {
      await this.connection.stop();
    }
  }

  private receiveMessage(message: AiMessage): void {
    if (this.disposed) return;
    const turn = this.activeTurn;
    const messageExecutionId = readString(message.additionalProperties?.executionId);
    if (turn && messageExecutionId === turn.executionId) {
      turn.durableConfirmed = true;
      turn.streamCursor =
        readString(message.additionalProperties?.streamCursor) ?? turn.streamCursor;
    }

    const terminal = getTurnFinishedStatus(message);
    if (terminal) {
      if (turn && (!messageExecutionId || messageExecutionId === turn.executionId)) {
        this.finishActiveTurn(terminal === "failed" ? new Error(readFailure(message)) : undefined);
      }
      return;
    }

    this.options.onMessage(message);
  }

  private async dispatch(command: unknown): Promise<void> {
    await this.ensureConnected();
    if (this.disposed) throw new Error("Execution session is disposed.");
    await this.connection.invoke("DispatchCommand", command);
  }

  private async ensureConnected(): Promise<void> {
    if (this.disposed) throw new Error("Execution session is disposed.");
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
      throw new Error(`Execution connection is ${this.connection.state}.`);
    }

    this.startPromise ??= this.connection.start().finally(() => {
      this.startPromise = null;
    });
    await this.startPromise;
    if (this.disposed) throw new Error("Execution session is disposed.");
  }

  private beginReconnect(): void {
    if (this.reconnectCompletion) return;
    let resolve!: (error: Error | null) => void;
    const promise = new Promise<Error | null>((complete) => {
      resolve = complete;
    });
    this.reconnectCompletion = { promise, resolve };
  }

  private finishReconnect(error: Error | null): void {
    const completion = this.reconnectCompletion;
    this.reconnectCompletion = null;
    completion?.resolve(error);
  }

  private async waitForReconnect(): Promise<void> {
    const completion = this.reconnectCompletion;
    if (!completion) return;
    const error = await completion.promise;
    if (error) throw error;
    if (this.connection.state !== HubConnectionState.Connected) {
      throw new Error(`Execution connection is ${this.connection.state}.`);
    }
  }

  private async restoreAfterReconnect(): Promise<void> {
    if (this.disposed || !this.setting) return;
    await this.refreshProvider();
    await this.connection.invoke("DispatchCommand", buildMobileSettingCommand(this.setting));

    const turn = this.activeTurn;
    if (!turn) return;
    if (this.provider === "in-process") {
      throw new Error(
        "The connection recovered, but an in-process execution cannot resume streaming.",
      );
    }
    if (this.provider !== "distributed" && !turn.durableConfirmed) {
      throw new Error("The server cannot confirm that this execution can resume streaming.");
    }
    await this.subscribeActiveTurn(turn);
  }

  private finishReconnectSuccessfully(): void {
    this.reconnecting = false;
    this.reconnectState = null;
    this.finishReconnect(null);
    this.options.onReconnecting?.(null);
  }

  private failReconnect(error: Error): void {
    this.reconnecting = false;
    this.reconnectState = null;
    this.finishReconnect(error);
    this.options.onReconnecting?.(null);
    this.finishActiveTurn(error);
  }

  private async subscribeActiveTurn(turn: ActiveTurn): Promise<void> {
    await this.connection.invoke(
      "DispatchCommand",
      buildSubscribeExecutionCommand(turn.executionId, turn.streamCursor),
    );
  }

  private async refreshProvider(): Promise<void> {
    try {
      const value = (await this.connection.invoke<string>("GetExecutionProvider")).toLowerCase();
      this.provider =
        value === "distributed" ? "distributed" : value === "inprocess" ? "in-process" : null;
    } catch {
      this.provider = null;
    }
  }

  private finishActiveTurn(error?: Error): void {
    const turn = this.activeTurn;
    if (!turn) return;
    this.activeTurn = null;
    if (error) turn.reject(error);
    else turn.resolve();
  }
}

function createActiveTurn(executionId: string, durableConfirmed: boolean): ActiveTurn {
  let resolve!: () => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<void>((complete, fail) => {
    resolve = complete;
    reject = fail;
  });
  return {
    executionId,
    durableConfirmed,
    streamCursor: null,
    promise,
    resolve,
    reject,
  };
}

function readString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value : undefined;
}

function readFailure(message: AiMessage): string {
  const content = message.contents[0]?.content;
  return typeof content === "string" && content.trim() ? content : "Execution failed.";
}

function toError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}
