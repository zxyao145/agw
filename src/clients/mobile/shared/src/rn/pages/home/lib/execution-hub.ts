import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  HttpTransportType,
  LogLevel,
} from "@microsoft/signalr";

import type { AgwMessage, AgwMessageContent } from "../../../api/agw-api-types";

export type ExecutionHubUserInput = Pick<AgwMessage, "messageId" | "author" | "contents">;

export type ExecutionHubSettingCommandRequest = {
  projectId: string;
  contextId: string;
  environmentVariables?: Record<string, string> | null;
};

export type ExecutionHubRequest = ExecutionHubSettingCommandRequest & {
  agentId: string;
  agentType: number;
  stream?: boolean;
  input: ExecutionHubUserInput;
};

export type ExecutionHubHandlers = {
  onMessage: (message: AgwMessage) => void;
  onError?: (error: Error) => void;
  onClose?: (error?: Error) => void;
};

function buildExecutionHubUrl(serverUrl: string): string {
  const normalizedBaseUrl = serverUrl.replace(/\/+$/g, "");
  const parsed = new URL(normalizedBaseUrl);
  const basePath = parsed.pathname === "/" ? "" : parsed.pathname.replace(/\/+$/g, "");
  return `${parsed.protocol}//${parsed.host}${basePath}/api/hubs/exec`;
}

function cloneAdditionalProperties(
  additionalProperties: Record<string, unknown> | null | undefined,
): Record<string, unknown> | null | undefined {
  if (additionalProperties === null || additionalProperties === undefined) {
    return additionalProperties;
  }

  return { ...additionalProperties };
}

function cloneMessageContent(content: AgwMessageContent): AgwMessageContent {
  return {
    ...content,
    additionalProperties: cloneAdditionalProperties(content.additionalProperties),
  };
}

function cloneMessage(message: AgwMessage): AgwMessage {
  return {
    ...message,
    additionalProperties: cloneAdditionalProperties(message.additionalProperties),
    contents: message.contents.map(cloneMessageContent),
  };
}

export function toExecutionHubUserInput(message: AgwMessage): ExecutionHubUserInput {
  return {
    messageId: message.messageId,
    author: message.author,
    contents: message.contents.map(cloneMessageContent),
  };
}

const TEXT_CONTENT_TYPES = new Set(["TextContent", "text"]);

function isTextContent(content: AgwMessageContent): boolean {
  return TEXT_CONTENT_TYPES.has(content.type);
}

function getFirstTextContent(contents: AgwMessageContent[]): AgwMessageContent | undefined {
  return contents.find(isTextContent);
}

function getNonTextContents(contents: AgwMessageContent[]): AgwMessageContent[] {
  return contents.filter((content) => !isTextContent(content)).map(cloneMessageContent);
}

function mergeStreamingMessage(
  currentMessages: AgwMessage[],
  incomingMessage: AgwMessage,
): AgwMessage[] {
  const index = currentMessages.findIndex(
    (message) => message.messageId === incomingMessage.messageId,
  );
  if (index === -1) {
    return [...currentMessages, cloneMessage(incomingMessage)];
  }

  const merged = [...currentMessages];
  const current = cloneMessage(merged[index]);
  const incomingText = getFirstTextContent(incomingMessage.contents);
  const currentText = getFirstTextContent(current.contents);

  if (incomingText) {
    if (currentText) {
      currentText.content = `${currentText.content ?? ""}${incomingText.content ?? ""}`;
    } else {
      current.contents.push(cloneMessageContent(incomingText));
    }
  }

  const nonTextContents = getNonTextContents(incomingMessage.contents);
  if (nonTextContents.length > 0) {
    current.contents = [...current.contents, ...nonTextContents];
  }

  if (incomingMessage.additionalProperties !== undefined) {
    current.additionalProperties = cloneAdditionalProperties(incomingMessage.additionalProperties);
  }

  merged[index] = current;
  return merged;
}

export function mergeStreamingMessages(
  currentMessages: AgwMessage[],
  incomingMessages: AgwMessage[],
): AgwMessage[] {
  return incomingMessages.reduce<AgwMessage[]>(
    (nextMessages, incomingMessage) => mergeStreamingMessage(nextMessages, incomingMessage),
    [...currentMessages],
  );
}

export function buildSettingCommand(request: ExecutionHubSettingCommandRequest) {
  return {
    type: "SettingCommand" as const,
    projectId: request.projectId,
    contextId: request.contextId,
    ...(request.environmentVariables === undefined
      ? {}
      : { environmentVariables: request.environmentVariables }),
  };
}

export function buildExecCommand(request: ExecutionHubRequest) {
  return {
    type: "ExecCommand" as const,
    agentId: request.agentId,
    agentType: request.agentType,
    stream: request.stream ?? true,
    input: request.input,
  };
}

export function isTurnFinishedMessage(message: AgwMessage): boolean {
  const status = message.additionalProperties?.status;
  return (
    message.additionalProperties?.type === "turn-finished" ||
    message.contents.some((content) => content.additionalProperties?.type === "turn-finished") ||
    status === "completed" ||
    status === "interrupted" ||
    status === "failed"
  );
}

export class ExecutionHubClient {
  private readonly connection: HubConnection;
  private handlers: ExecutionHubHandlers;
  private disposed = false;

  public constructor(serverUrl: string, token: string, handlers: ExecutionHubHandlers) {
    this.handlers = handlers;
    this.connection = new HubConnectionBuilder()
      .withUrl(buildExecutionHubUrl(serverUrl), {
        accessTokenFactory: () => token,
        transport: HttpTransportType.WebSockets,
      })
      .configureLogging(LogLevel.Warning)
      .build();

    this.connection.on("ReceiveMessage", (message: AgwMessage) => {
      if (!this.disposed) this.handlers.onMessage(message);
    });
    this.connection.onclose((error) => {
      if (!this.disposed) this.handlers.onClose?.(error);
    });
  }

  public async configure(setting: ExecutionHubSettingCommandRequest): Promise<void> {
    await this.dispatch(buildSettingCommand(setting));
  }

  public async execute(request: ExecutionHubRequest): Promise<void> {
    await this.dispatch(buildExecCommand(request));
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
}
