import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  HttpTransportType,
  LogLevel,
} from "@microsoft/signalr";

import type { AiMessage } from "@/types";

export type ExecutionRuntimeConfig = {
  baseUrl: string;
  token: string | null;
};

let executionRuntime: ExecutionRuntimeConfig = { baseUrl: "", token: null };

export function configureExecutionRuntime(config: ExecutionRuntimeConfig): void {
  executionRuntime = config;
}

export type ExecutionUserInput = Pick<AiMessage, "messageId" | "author" | "contents">;

export type ExecutionSetting = {
  projectId: string;
  contextId: string;
  environmentVariables?: Record<string, string> | null;
};

export type ExecutionTarget = {
  agentId: string;
  agentType: number;
};

export type ExecutionRequest = ExecutionTarget & {
  stream?: boolean;
  input: ExecutionUserInput;
};

export type PendingHumanGate = {
  requestId: string;
  nodeId: string;
  nodeName?: string;
  mode: string;
  prompt: string;
  inputPreview?: string;
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
  };
}

export function buildExecCommand(request: ExecutionRequest) {
  return {
    type: "ExecCommand" as const,
    agentId: request.agentId,
    agentType: request.agentType,
    stream: request.stream ?? true,
    input: request.input,
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
  if (message.additionalProperties?.type !== "human-gate-request") return null;
  const requestId = readString(message.additionalProperties.requestId);
  const nodeId = readString(message.additionalProperties.nodeId);
  if (!requestId || !nodeId) return null;

  return {
    requestId,
    nodeId,
    nodeName: readString(message.additionalProperties.nodeName),
    mode: readString(message.additionalProperties.mode) ?? "approval",
    prompt:
      readString(message.additionalProperties.prompt) ??
      readString(message.contents[0]?.content) ??
      "Human approval is required to continue.",
    inputPreview: readString(message.additionalProperties.inputPreview),
  };
}

const executionInterruptTimeoutMs = 3_000;

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
  private handlers: ExecutionHubHandlers;
  private disposed = false;
  private hasActiveTurn = false;
  private readonly turnFinishedWaiters = new Set<() => void>();

  public constructor(
    handlers: ExecutionHubHandlers,
    runtime: ExecutionRuntimeConfig = executionRuntime,
  ) {
    this.handlers = handlers;
    const hub = buildExecutionHubOptions(runtime);
    this.connection = new HubConnectionBuilder()
      .withUrl(hub.url, hub.options)
      .configureLogging(LogLevel.Warning)
      .build();

    this.connection.on("ReceiveMessage", (message: AiMessage) => {
      if (message.additionalProperties?.type === "turn-start") {
        this.hasActiveTurn = true;
      } else if (getTurnFinishedStatus(message)) {
        this.finishActiveTurn();
      }

      if (!this.disposed) this.handlers.onMessage(message);
    });
    this.connection.onclose((error) => {
      this.finishActiveTurn();
      if (!this.disposed) this.handlers.onClose?.(error);
    });
  }

  public setHandlers(handlers: ExecutionHubHandlers): void {
    this.handlers = handlers;
  }

  public async configure(setting: ExecutionSetting): Promise<void> {
    await this.dispatch(buildSettingCommand(setting));
  }

  public async execute(request: ExecutionRequest): Promise<void> {
    this.hasActiveTurn = true;
    try {
      await this.dispatch(buildExecCommand(request));
    } catch (error) {
      this.finishActiveTurn();
      throw error;
    }
  }

  public async interrupt(reason?: string): Promise<void> {
    await this.dispatch({ type: "InterruptCommand", reason });
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
  }): Promise<void> {
    await this.dispatch({ type: "HumanResponseCommand", ...args });
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

  private finishActiveTurn(): void {
    this.hasActiveTurn = false;
    for (const resolve of this.turnFinishedWaiters) {
      resolve();
    }
    this.turnFinishedWaiters.clear();
  }
}
