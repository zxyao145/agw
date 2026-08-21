import type { AiMessage } from "@agw/api";
import {
  HubConnectionBuilder,
  HubConnectionState,
  HttpTransportType,
  LogLevel,
  type IHttpConnectionOptions,
  type IRetryPolicy,
} from "@microsoft/signalr";
import {
  buildExecCommand,
  buildInterruptCommand,
  buildSettingCommand,
  buildSubscribeExecutionCommand,
  getExecutionReconnectDelay,
  getTurnFinishedStatus,
  type ExecutionUserInput,
  type PermissionMode,
} from "@agw/execution-core";

export type MobileExecutionRequest = {
  projectId: string;
  contextId: string;
  agentId: string;
  agentType: 0 | 1;
  executionId: string;
  permissionMode: PermissionMode;
  agentMode: AgentMode;
  input: ExecutionUserInput<AiMessage>;
};

export type AgentMode = "plan" | "execute";

export type ExecutionReconnectState = {
  retryAttempt: number;
  retryDelayMs: number;
};

export type MobileExecutionHandle = {
  promise: Promise<void>;
  interrupt(reason?: string): Promise<void>;
  close(): void;
};

export function buildMobileSettingCommand(
  request: Pick<MobileExecutionRequest, "projectId" | "contextId" | "permissionMode">,
) {
  return buildSettingCommand({
    projectId: request.projectId,
    contextId: request.contextId,
    permissionMode: request.permissionMode,
  });
}

export function buildMobileModeCommand(
  request: Pick<MobileExecutionRequest, "agentId" | "agentType" | "agentMode">,
) {
  if (request.agentType !== 0) return null;

  return {
    type: "SetModeCommand" as const,
    agentId: request.agentId,
    mode: request.agentMode,
  };
}

type ProviderCapability = "distributed" | "in-process" | null;

const serverTimeoutMs = 30_000;

export function executeWithWebSocket(args: {
  serverUrl: string;
  token: string;
  request: MobileExecutionRequest;
  onMessage(message: AiMessage): void;
  onReconnecting?(state: ExecutionReconnectState | null): void;
}): MobileExecutionHandle {
  const hubUrl = `${args.serverUrl.replace(/\/+$/u, "")}/api/hubs/exec`;
  const reconnectPolicy: IRetryPolicy = {
    nextRetryDelayInMilliseconds: (context) =>
      getExecutionReconnectDelay(context.previousRetryCount),
  };
  const options: IHttpConnectionOptions & { WebSocket: typeof WebSocket } = {
    transport: HttpTransportType.WebSockets,
    skipNegotiation: true,
    accessTokenFactory: async () => args.token,
    WebSocket: globalThis.WebSocket,
  };
  const connection = new HubConnectionBuilder()
    .withUrl(hubUrl, options)
    .configureLogging(LogLevel.Warning)
    .withAutomaticReconnect(reconnectPolicy)
    .build();
  connection.serverTimeoutInMilliseconds = serverTimeoutMs;

  let resolveExecution!: () => void;
  let rejectExecution!: (error: Error) => void;
  const promise = new Promise<void>((resolve, reject) => {
    resolveExecution = resolve;
    rejectExecution = reject;
  });
  let settled = false;
  let provider: ProviderCapability = null;
  let durableConfirmed = false;
  let streamCursor: string | null = null;

  const stopConnection = () => {
    if (connection.state !== HubConnectionState.Disconnected) {
      void connection.stop().catch(() => undefined);
    }
  };
  const settle = (error?: Error) => {
    if (settled) return;
    settled = true;
    args.onReconnecting?.(null);
    if (error) rejectExecution(error);
    else resolveExecution();
    stopConnection();
  };
  const refreshProvider = async () => {
    try {
      const value = (await connection.invoke<string>("GetExecutionProvider")).toLowerCase();
      provider =
        value === "distributed" ? "distributed" : value === "inprocess" ? "in-process" : null;
    } catch {
      provider = null;
    }
  };

  connection.on("ReceiveMessage", (message: AiMessage) => {
    if (settled) return;
    const messageExecutionId = readString(message.additionalProperties?.executionId);
    if (messageExecutionId === args.request.executionId) {
      durableConfirmed = true;
      streamCursor = readString(message.additionalProperties?.streamCursor) ?? streamCursor;
    }
    const terminal = getTurnFinishedStatus(message);
    if (terminal) {
      settle(terminal === "failed" ? new Error(readFailure(message)) : undefined);
      return;
    }
    args.onMessage(message);
  });

  connection.onreconnecting(() => {
    const retryAttempt = 1;
    const retryDelayMs = getExecutionReconnectDelay(0) ?? 0;
    args.onReconnecting?.({ retryAttempt, retryDelayMs });
  });
  connection.onreconnected(() => {
    void (async () => {
      if (settled) return;
      args.onReconnecting?.(null);
      await refreshProvider();
      await connection.invoke("DispatchCommand", buildMobileSettingCommand(args.request));
      if (provider === "in-process") {
        throw new Error(
          "The connection recovered, but an in-process execution cannot resume streaming.",
        );
      }
      if (provider !== "distributed" && !durableConfirmed) {
        throw new Error("The server cannot confirm that this execution can resume streaming.");
      }
      await connection.invoke(
        "DispatchCommand",
        buildSubscribeExecutionCommand(args.request.executionId, streamCursor),
      );
    })().catch((error) => settle(error instanceof Error ? error : new Error(String(error))));
  });
  connection.onclose((error) => {
    if (settled) return;
    settle(
      new Error(
        `Execution connection retries were exhausted.${error?.message ? ` ${error.message}` : ""}`,
      ),
    );
  });

  const initialization = (async () => {
    await connection.start();
    if (settled) return;
    await refreshProvider();
    if (provider === "distributed") durableConfirmed = true;
    await connection.invoke("DispatchCommand", buildMobileSettingCommand(args.request));
    const modeCommand = buildMobileModeCommand(args.request);
    if (modeCommand) await connection.invoke("DispatchCommand", modeCommand);
    await connection.invoke(
      "DispatchCommand",
      buildExecCommand({
        agentId: args.request.agentId,
        agentType: args.request.agentType,
        executionId: args.request.executionId,
        stream: true,
        input: args.request.input,
      }),
    );
  })();
  void initialization.catch((error) =>
    settle(error instanceof Error ? error : new Error(String(error))),
  );

  return {
    promise,
    interrupt: async (reason) => {
      await initialization;
      if (settled || connection.state !== HubConnectionState.Connected) {
        throw new Error("Execution connection is not ready.");
      }
      await connection.invoke(
        "DispatchCommand",
        buildInterruptCommand(args.request.executionId, reason),
      );
    },
    close: () => settle(),
  };
}

function readString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value : undefined;
}

function readFailure(message: AiMessage): string {
  const content = message.contents[0]?.content;
  return typeof content === "string" && content.trim() ? content : "Execution failed.";
}
