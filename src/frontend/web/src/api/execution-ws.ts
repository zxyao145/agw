import { getApiKey } from "@/api/client";
import type { AiMessage } from "@/types";

export type ExecutionWsUserInput = Pick<AiMessage, "messageId" | "author" | "contents">;

export type ExecutionWsEnvironmentVariables = Record<string, string>;

export type ExecutionWsSettingCommandRequest = {
  projectId: string;
  taskId?: string | null;
  contextId?: string | null;
  resume?: boolean;
  environmentVariables?: ExecutionWsEnvironmentVariables | null;
};

export type ExecutionWsSettingCommandPayload = {
  type: "SettingCommand";
  projectId: string;
  taskId: string | null;
  contextId: string | null;
  resume: boolean;
  environmentVariables?: ExecutionWsEnvironmentVariables | null;
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

function buildExecutionWsUrls(id: string): string[] {
  const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
  const apiKey = getApiKey();
  const apiKeyParam = apiKey ? `?X-API-Key=${encodeURIComponent(apiKey)}` : "";
  const url = `${protocol}//${window.location.host}/api/executions/${id}/ws${apiKeyParam}`;
  const urls: string[] = [];
  urls.push(url);
  return urls;
}

function tryParseExecutionWsResult(payload: string): ExecutionWsResult | null {
  try {
    const message = JSON.parse(payload) as AiMessage;
    if (message.role !== "system") {
      return null;
    }

    const status = message.additionalProperties?.status;
    if (
      status !== "completed" &&
      status !== "interrupted" &&
      status !== "cancelled" &&
      status !== "failed"
    ) {
      return null;
    }

    return {
      status,
      message:
        typeof message.contents?.[0]?.content === "string"
          ? message.contents[0].content
          : "Execution completed",
    };
  } catch {
    return null;
  }
}

export function buildSettingCommandPayload(
  request: ExecutionWsSettingCommandRequest,
): ExecutionWsSettingCommandPayload {
  const payload: ExecutionWsSettingCommandPayload = {
    type: "SettingCommand",
    projectId: request.projectId,
    taskId: request.taskId ?? null,
    contextId: request.contextId ?? null,
    resume: request.resume ?? false,
  };

  if (request.environmentVariables !== undefined) {
    payload.environmentVariables = request.environmentVariables;
  }

  return payload;
}

function openExecutionWebSocket(
  wsUrl: string,
  request: ExecutionWsRequest,
  onMessage: (data: string) => void,
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
        }),
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

        if (ws.readyState === WebSocket.OPEN) {
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
  id: string,
  request: ExecutionWsRequest,
  onMessage: (data: string) => void,
): Promise<void> {
  const urls = buildExecutionWsUrls(id);
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
