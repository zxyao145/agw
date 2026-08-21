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
  buildSettingCommand,
  buildSubscribeExecutionCommand,
  cloneMessageContent,
  executionReconnectDelaysMs,
  getExecutionReconnectDelay,
  getTurnFinishedStatus,
  mergeStreamingMessages,
  type ExecutionUserInput,
  type PermissionMode,
} from "@agw/execution-core";
import type { AgwMessage } from "../../../api/agw-api-types";

export { mergeStreamingMessages };

export type ExecutionWsUserInput = ExecutionUserInput<AgwMessage>;
export type ExecutionWsPermissionMode = PermissionMode;

export type ExecutionWsSettingCommandRequest = {
  projectId: string;
  contextId?: string | null;
  environmentVariables?: Record<string, string> | null;
  permissionMode?: ExecutionWsPermissionMode | null;
};

export type ExecutionWsRequest = ExecutionWsSettingCommandRequest & {
  agentId: string;
  agentType: number;
  /** Mobile 为每次执行生成稳定 ID，以便 distributed provider 断线后重新订阅。 */
  executionId: string;
  stream?: boolean;
  input: ExecutionWsUserInput;
};

type ExecutionProviderCapability = "in-process" | "distributed" | null;

const SIGNALR_SERVER_TIMEOUT_MS = 30_000;

export function toExecutionWsUserInput(message: AgwMessage): ExecutionWsUserInput {
  return {
    messageId: message.messageId,
    author: message.author,
    contents: message.contents.map(cloneMessageContent),
  };
}

/** 兼容既有 Mobile 导出；命令形状由 execution-core 统一维护。 */
export function buildSettingCommandPayload(request: ExecutionWsSettingCommandRequest) {
  return buildSettingCommand(request);
}

/** 兼容既有 Mobile 导出；命令形状由 execution-core 统一维护。 */
export function buildExecCommandPayload(request: ExecutionWsRequest) {
  return buildExecCommand(request);
}

/** 兼容既有 Mobile 导出；命令形状由 execution-core 统一维护。 */
export function buildInterruptCommandPayload(executionId?: string, reason?: string) {
  return buildInterruptCommand(executionId, reason);
}

export type ExecutionWsHandle = {
  promise: Promise<void>;
  interrupt: (reason?: string) => Promise<void>;
  close: () => void;
};

function normalizeExecutionProvider(value: string): ExecutionProviderCapability {
  const provider = value.toLowerCase();
  if (provider === "distributed") return "distributed";
  if (provider === "inprocess") return "in-process";
  return null;
}

function readString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim().length > 0 ? value : undefined;
}

function getExecutionFailureMessage(message: AgwMessage): string {
  const content = message.contents[0]?.content;
  return typeof content === "string" && content.trim().length > 0 ? content : "Execution failed";
}

function stopConnection(connection: HubConnection): void {
  if (connection.state !== HubConnectionState.Disconnected) {
    void connection.stop().catch(() => undefined);
  }
}

export function executeWithWebSocket(
  serverUrl: string,
  token: string,
  request: ExecutionWsRequest,
  onMessage: (message: AgwMessage) => void,
): ExecutionWsHandle {
  const hubUrl = `${serverUrl.replace(/\/+$/u, "")}/api/hubs/exec`;
  const reconnectPolicy: IRetryPolicy = {
    nextRetryDelayInMilliseconds: (context) =>
      getExecutionReconnectDelay(context.previousRetryCount),
  };
  const connectionOptions: IHttpConnectionOptions & { WebSocket: typeof WebSocket } = {
    transport: HttpTransportType.WebSockets,
    skipNegotiation: true,
    accessTokenFactory: async () => token,
    // SignalR marks this injection hook internal, but passing RN's implementation also keeps Jest on
    // the same transport path instead of silently selecting the Node `ws` package.
    WebSocket: globalThis.WebSocket,
  };
  const connection = new HubConnectionBuilder()
    .withUrl(hubUrl, connectionOptions)
    .configureLogging(LogLevel.Warning)
    .withAutomaticReconnect(reconnectPolicy)
    .build();
  connection.serverTimeoutInMilliseconds = SIGNALR_SERVER_TIMEOUT_MS;

  let resolveExecution!: () => void;
  let rejectExecution!: (error: Error) => void;
  const promise = new Promise<void>((resolve, reject) => {
    resolveExecution = resolve;
    rejectExecution = reject;
  });
  let settled = false;
  let provider: ExecutionProviderCapability = null;
  let durableConfirmed = false;
  let streamCursor: string | null = null;

  const settle = (error?: Error) => {
    if (settled) return;
    settled = true;
    if (error) rejectExecution(error);
    else resolveExecution();
    stopConnection(connection);
  };

  const refreshExecutionProvider = async () => {
    try {
      provider = normalizeExecutionProvider(
        await connection.invoke<string>("GetExecutionProvider"),
      );
    } catch {
      // 兼容尚未提供 capability Hub method 的服务端；消息中的 executionId 仍可确认 durable。
      provider = null;
    }
  };

  connection.on("ReceiveMessage", (message: AgwMessage) => {
    if (settled) return;

    const messageExecutionId = readString(message.additionalProperties?.executionId);
    if (messageExecutionId === request.executionId) {
      durableConfirmed = true;
      streamCursor = readString(message.additionalProperties?.streamCursor) ?? streamCursor;
    }

    const terminalStatus = getTurnFinishedStatus(message);
    if (terminalStatus) {
      settle(
        terminalStatus === "failed" ? new Error(getExecutionFailureMessage(message)) : undefined,
      );
      return;
    }

    onMessage(message);
  });

  connection.onreconnected(() => {
    void (async () => {
      if (settled) return;
      await refreshExecutionProvider();
      await connection.invoke("DispatchCommand", buildSettingCommandPayload(request));

      if (provider === "in-process") {
        throw new Error(
          "Execution connection was restored, but an in-process execution cannot resume streaming; it may still be running on the server.",
        );
      }
      if (provider !== "distributed" && !durableConfirmed) {
        throw new Error(
          "Execution connection was restored, but the server cannot confirm that this execution is resumable; it may still be running on the server.",
        );
      }

      await connection.invoke(
        "DispatchCommand",
        buildSubscribeExecutionCommand(request.executionId, streamCursor),
      );
    })().catch((error) => {
      settle(error instanceof Error ? error : new Error(String(error)));
    });
  });

  connection.onclose((error) => {
    if (settled) return;
    const detail = error?.message ? ` ${error.message}` : "";
    settle(
      new Error(
        `Execution connection retries exhausted; the execution may still be running on the server.${detail}`,
      ),
    );
  });

  const initialize = async () => {
    await connection.start();
    if (settled) return;
    await refreshExecutionProvider();
    if (settled) return;
    if (provider === "distributed") durableConfirmed = true;
    await connection.invoke("DispatchCommand", buildSettingCommandPayload(request));
    if (settled) return;
    await connection.invoke("DispatchCommand", buildExecCommandPayload(request));
  };
  const initialization = initialize();
  void initialization.catch((error) => {
    settle(error instanceof Error ? error : new Error(String(error)));
  });

  return {
    promise,
    interrupt: async (reason?: string) => {
      await initialization;
      if (settled || connection.state !== HubConnectionState.Connected) {
        throw new Error("Execution connection is not ready.");
      }
      await connection.invoke(
        "DispatchCommand",
        buildInterruptCommandPayload(request.executionId, reason),
      );
    },
    close: () => settle(),
  };
}

export { executionReconnectDelaysMs };
