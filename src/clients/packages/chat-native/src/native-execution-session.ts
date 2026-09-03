import type { AiMessage } from "@agw/api";
import {
  ExecutionSession,
  type ExecutionReconnectState,
} from "@agw/chat-runtime/execution-session";
import {
  buildSettingCommand,
  type AgentMode,
  type ExecutionUserInput,
  type PermissionMode,
  type TurnFinishedStatus,
} from "@agw/execution-core";
import { getTurnFinishedStatus } from "@agw/execution-core";
import type { AgentflowCheckpointAvailability } from "@agw/chat-core";

export type NativeExecutionSetting = {
  projectId: string;
  contextId: string;
  permissionMode: PermissionMode;
};

export type NativeExecutionRequest = {
  conversationId: string;
  agentId: string;
  agentType: 0 | 1;
  executionId: string;
  input: ExecutionUserInput<AiMessage>;
};

export type NativeExecutionSessionOptions = {
  serverUrl: string;
  token: string;
  onMessage(message: AiMessage): void;
  onClose?(error: Error): void;
  onReconnecting?(state: ExecutionReconnectState | null): void;
};

export function buildMobileSettingCommand(setting: NativeExecutionSetting) {
  return buildSettingCommand(setting);
}

type TerminalWaiter = {
  executionId: string;
  promise: Promise<void>;
  resolve(): void;
  reject(error: Error): void;
};

export class NativeExecutionSession {
  private readonly session: ExecutionSession;
  private readonly options: NativeExecutionSessionOptions;
  private terminal: TerminalWaiter | null = null;

  public constructor(options: NativeExecutionSessionOptions) {
    this.options = options;
    this.session = new ExecutionSession(
      {
        onMessage: (message) => this.handleMessage(message),
        onClose: (error) => {
          const normalized = error ?? new Error("Execution connection closed.");
          this.finishTerminal(null, normalized);
          this.options.onClose?.(normalized);
        },
        onReconnecting: (state) => this.options.onReconnecting?.(state),
        onReconnectFailed: (state) => this.options.onReconnecting?.(state),
        onReconnected: () => this.options.onReconnecting?.(null),
      },
      {
        baseUrl: options.serverUrl,
        token: options.token,
        withCredentials: false,
        webSocket: globalThis.WebSocket,
        attachmentStore: null,
      },
    );
  }

  public async configure(setting: NativeExecutionSetting): Promise<void> {
    await this.session.configure(setting);
  }

  public async execute(request: NativeExecutionRequest): Promise<void> {
    const terminal = this.beginTerminal(request.executionId);
    try {
      await this.session.execute({ ...request, stream: true });
      await terminal.promise;
    } catch (error) {
      this.finishTerminal(request.executionId, toError(error));
      throw error;
    }
  }

  public async setMode(agentId: string, mode: AgentMode): Promise<void> {
    await this.session.setMode(agentId, mode);
  }

  public async setPermissionMode(permissionMode: PermissionMode): Promise<void> {
    await this.session.setPermissionMode(permissionMode);
  }

  public async interrupt(reason?: string): Promise<void> {
    await this.session.interrupt(reason);
  }

  public async submitHumanResponse(args: {
    requestId: string;
    approved: boolean;
    responseText?: string | null;
    approvalScope?: "once" | "always-tool" | "always-arguments";
    responseData?: unknown;
  }): Promise<void> {
    await this.session.submitHumanResponse(args);
  }

  public async listAgentflowCheckpoints(
    agentflowId: string,
  ): Promise<AgentflowCheckpointAvailability[]> {
    return this.session.listAgentflowCheckpoints(agentflowId);
  }

  public async resumeCheckpoint(args: {
    checkpointOccurrenceId: string;
    agentflowId: string;
    resumeExecutionId: string;
  }): Promise<void> {
    await this.session.resumeCheckpoint(args);
  }

  public async dispose(): Promise<void> {
    this.finishTerminal(null, new Error("Execution session was disposed."));
    await this.session.dispose();
  }

  private handleMessage(message: AiMessage): void {
    const status = getTurnFinishedStatus(message);
    if (status) this.finishTerminal(readExecutionId(message), terminalError(status, message));
    this.options.onMessage(message);
  }

  private beginTerminal(executionId: string): TerminalWaiter {
    if (this.terminal) throw new Error("This conversation already has a running task.");
    let resolve!: () => void;
    let reject!: (error: Error) => void;
    const promise = new Promise<void>((complete, fail) => {
      resolve = complete;
      reject = fail;
    });
    const terminal = { executionId, promise, resolve, reject };
    this.terminal = terminal;
    return terminal;
  }

  private finishTerminal(executionId: string | null, error?: Error): void {
    const terminal = this.terminal;
    if (!terminal || (executionId && terminal.executionId !== executionId)) return;
    this.terminal = null;
    if (error) terminal.reject(error);
    else terminal.resolve();
  }
}

function readExecutionId(message: AiMessage): string | null {
  const value = message.additionalProperties?.executionId;
  return typeof value === "string" && value.trim() ? value : null;
}

function terminalError(status: TurnFinishedStatus, message: AiMessage): Error | undefined {
  if (status !== "failed") return undefined;
  const content = message.contents[0]?.content;
  return new Error(typeof content === "string" && content.trim() ? content : "Execution failed.");
}

function toError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}

export { NativeExecutionSession as MobileExecutionSession };
export type {
  NativeExecutionRequest as MobileExecutionRequest,
  NativeExecutionSessionOptions as MobileExecutionSessionOptions,
  NativeExecutionSetting as MobileExecutionSetting,
};
export type { ExecutionReconnectState };
