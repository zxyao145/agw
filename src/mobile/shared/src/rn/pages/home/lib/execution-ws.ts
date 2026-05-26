import type { AgwMessage, AgwMessageContent } from "../../../api/agw-api-types";

export type ExecutionWsUserInput = Pick<
  AgwMessage,
  "messageId" | "author" | "contents"
>;

export type ExecutionWsSettingCommandRequest = {
  projectId: string;
  taskId: string;
  resume?: boolean;
  workspace?: string | null;
  settingContent?: string;
  environmentVariables?: Record<string, string> | null;
};

export type ExecutionWsSettingCommandPayload = {
  type: "SettingCommand";
  settingContent: string;
  projectId: string;
  taskId: string;
  resume: boolean;
  workspace?: string | null;
  environmentVariables?: Record<string, string> | null;
};

export type ExecutionWsRequest = ExecutionWsSettingCommandRequest & {
  agentType: number;
  input: ExecutionWsUserInput;
};

type ExecutionWsResultStatus = "completed" | "interrupted" | "cancelled" | "failed";

type ExecutionWsResult = {
  status: ExecutionWsResultStatus;
  message: string;
};

const TEXT_CONTENT_TYPES = new Set(["TextContent", "text"]);
const RESULT_STATUSES = new Set<ExecutionWsResultStatus>([
  "completed",
  "interrupted",
  "cancelled",
  "failed",
]);

function buildExecutionWebSocketUrls(
  serverDomain: string,
  agentId: string
): string[] {
  const normalizedBaseUrl = serverDomain.replace(/\/+$/g, "");
  const parsed = new URL(normalizedBaseUrl);
  const basePath = parsed.pathname === "/" ? "" : parsed.pathname.replace(/\/+$/g, "");
  const protocol =
    parsed.protocol === "https:"
      ? "wss:"
      : parsed.protocol === "wss:"
        ? "wss:"
        : parsed.protocol === "ws:"
          ? "ws:"
          : "ws:";

  return [
    `${protocol}//${parsed.host}${basePath}/api/executions/${encodeURIComponent(
      agentId
    )}/ws`,
  ];
}

function cloneAdditionalProperties(
  additionalProperties: Record<string, unknown> | null | undefined
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

export function toExecutionWsUserInput(message: AgwMessage): ExecutionWsUserInput {
  return {
    messageId: message.messageId,
    author: message.author,
    contents: message.contents.map(cloneMessageContent),
  };
}

export function parseExecutionWsMessage(payload: string): AgwMessage | null {
  try {
    return JSON.parse(payload) as AgwMessage;
  } catch (error) {
    return null;
  }
}

function tryParseExecutionWsResult(payload: string): ExecutionWsResult | null {
  const message = parseExecutionWsMessage(payload);
  if (!message || message.role !== "system") {
    return null;
  }

  const status = message.additionalProperties?.status ?? message.contents[0]?.additionalProperties?.status;
  const contentType = message.contents[0]?.additionalProperties?.type;
  const hasTurnFinishedType = message.contents.some(
    (content) => content.additionalProperties?.type === "turn-finished"
  );

  if (typeof status !== "string" || !RESULT_STATUSES.has(status as ExecutionWsResultStatus)) {
    if (contentType === "turn-finished" || hasTurnFinishedType) {
      const content = message.contents[0]?.content;
      return {
        message: typeof content === "string" ? content : "Execution completed",
        status: "completed",
      };
    }

    return null;
  }

  const content = message.contents[0]?.content;
  const messageContent = typeof content === "string" ? content : "Execution completed";

  return {
    message: messageContent,
    status: status as ExecutionWsResultStatus,
  };
}

function isTextContent(content: AgwMessageContent): boolean {
  return TEXT_CONTENT_TYPES.has(content.type);
}

function getFirstTextContent(contents: AgwMessageContent[]): AgwMessageContent | undefined {
  return contents.find(isTextContent);
}

function getNonTextContents(
  contents: AgwMessageContent[]
): AgwMessageContent[] {
  return contents.filter((content) => !isTextContent(content)).map(cloneMessageContent);
}

function mergeStreamingMessage(
  currentMessages: AgwMessage[],
  incomingMessage: AgwMessage
): AgwMessage[] {
  const index = currentMessages.findIndex(
    (message) => message.messageId === incomingMessage.messageId
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
    current.additionalProperties = cloneAdditionalProperties(
      incomingMessage.additionalProperties
    );
  }

  merged[index] = current;
  return merged;
}

export function mergeStreamingMessages(
  currentMessages: AgwMessage[],
  incomingMessages: AgwMessage[]
): AgwMessage[] {
  return incomingMessages.reduce<AgwMessage[]>(
    (nextMessages, incomingMessage) => mergeStreamingMessage(nextMessages, incomingMessage),
    [...currentMessages]
  );
}

export function buildSettingCommandPayload(
  request: ExecutionWsSettingCommandRequest
): ExecutionWsSettingCommandPayload {
  return {
    type: "SettingCommand",
    settingContent: request.settingContent ?? "{}",
    projectId: request.projectId,
    taskId: request.taskId,
    resume: request.resume ?? false,
    workspace: request.workspace,
    environmentVariables: request.environmentVariables,
  };
}

function openExecutionWebSocket(
  wsUrl: string,
  request: ExecutionWsRequest,
  onMessage: (data: string) => void
): Promise<void> {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl);
    let settled = false;

    const fail = (message: string) => {
      if (settled) return;
      settled = true;
      reject(new Error(message));
    };

    ws.onopen = () => {
      ws.send(JSON.stringify(buildSettingCommandPayload(request)));
      ws.send(
        JSON.stringify({
          type: "ExecCommand",
          agentType: request.agentType,
          input: request.input,
        })
      );
    };

    ws.onmessage = (event) => {
      if (typeof event.data !== "string") {
        return;
      }

      const result = tryParseExecutionWsResult(event.data);
      if (result) {
        if (settled) return;
        settled = true;
        if (ws.readyState === 1) {
          ws.close(1000, result.message);
        }

        if (result.status === "failed") {
          reject(new Error(result.message || "Execution failed"));
          return;
        }

        resolve();
        return;
      }

      onMessage(event.data);
    };

    ws.onerror = () => {
      fail("WebSocket connection error");
    };

    ws.onclose = (event) => {
      if (settled) return;
      settled = true;
      if (event.code === 1000) {
        resolve();
        return;
      }
      reject(new Error(event.reason || `WebSocket closed unexpectedly with code ${event.code}`));
    };
  });
}

export async function executeWithWebSocket(
  serverDomain: string,
  agentId: string,
  request: ExecutionWsRequest,
  onMessage: (data: string) => void
): Promise<void> {
  const urls = buildExecutionWebSocketUrls(serverDomain, agentId);
  let lastError: Error | null = null;

  for (const url of urls) {
    try {
      await openExecutionWebSocket(url, request, onMessage);
      return;
    } catch (error) {
      lastError = error instanceof Error ? error : new Error("WebSocket connection error");
    }
  }

  throw lastError ?? new Error("WebSocket connection error");
}
