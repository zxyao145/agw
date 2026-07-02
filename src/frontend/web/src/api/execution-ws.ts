import { getApiKey } from "./client";
import type { AiMessage } from "@/types";

export type ExecutionWsUserInput = Pick<AiMessage, "messageId" | "author" | "contents">;

export type ExecutionWsEnvironmentVariables = Record<string, string>;

export type ExecutionWsSettingCommandRequest = {
  projectId: string;
  contextId: string;
  resume?: boolean;
  environmentVariables?: ExecutionWsEnvironmentVariables | null;
};

export type ExecutionWsSettingCommandPayload = {
  type: "SettingCommand";
  projectId: string;
  contextId: string;
  resume: boolean;
  environmentVariables?: ExecutionWsEnvironmentVariables | null;
};

export type HumanGateRequest = {
  requestId: string;
  nodeId: string;
  nodeName?: string;
  mode: string;
  prompt: string;
  inputPreview?: string;
};

export type HumanGateResponse = {
  requestId: string;
  approved: boolean;
  responseText?: string | null;
};

export type ExecutionWsHumanResponseCommandPayload = {
  type: "HumanResponseCommand";
  requestId: string;
  approved: boolean;
  responseText?: string | null;
};

export type ExecutionWebSocketControls = {
  sendCommand: (payload: unknown) => boolean;
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

    if (message.additionalProperties?.type === "turn-finished") {
      return {
        status: "completed",
        message: "Execution completed",
      };
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

function readString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim().length > 0 ? value : undefined;
}

export function buildSettingCommandPayload(
  request: ExecutionWsSettingCommandRequest,
): ExecutionWsSettingCommandPayload {
  const payload: ExecutionWsSettingCommandPayload = {
    type: "SettingCommand",
    projectId: request.projectId,
    contextId: request.contextId,
    resume: request.resume ?? false,
  };

  if (request.environmentVariables !== undefined) {
    payload.environmentVariables = request.environmentVariables;
  }

  return payload;
}

export function buildHumanResponseCommandPayload(
  response: HumanGateResponse,
): ExecutionWsHumanResponseCommandPayload {
  const payload: ExecutionWsHumanResponseCommandPayload = {
    type: "HumanResponseCommand",
    requestId: response.requestId,
    approved: response.approved,
  };

  if (response.responseText !== undefined) {
    payload.responseText = response.responseText;
  }

  return payload;
}

export function getHumanGateRequest(message: AiMessage): HumanGateRequest | null {
  if (message.role !== "system" || message.additionalProperties?.type !== "human-gate-request") {
    return null;
  }

  const requestId = readString(message.additionalProperties.requestId);
  const nodeId = readString(message.additionalProperties.nodeId);
  if (!requestId || !nodeId) {
    return null;
  }

  const fallbackPrompt =
    typeof message.contents?.[0]?.content === "string"
      ? message.contents[0].content
      : "Human approval is required to continue.";
  const prompt = readString(message.additionalProperties.prompt) ?? fallbackPrompt;

  return {
    requestId,
    nodeId,
    nodeName: readString(message.additionalProperties.nodeName),
    mode: readString(message.additionalProperties.mode) ?? "approval",
    prompt,
    inputPreview: readString(message.additionalProperties.inputPreview),
  };
}

function openExecutionWebSocket(
  wsUrl: string,
  request: ExecutionWsRequest,
  onMessage: (data: string, controls: ExecutionWebSocketControls) => void,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl);
    let settled = false;

    const fail = (message: string) => {
      if (settled) return;
      settled = true;
      reject(new Error(message));
    };

    const controls: ExecutionWebSocketControls = {
      sendCommand: (payload) => {
        if (settled || ws.readyState !== WebSocket.OPEN) {
          return false;
        }

        ws.send(JSON.stringify(payload));
        return true;
      },
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

      onMessage(event.data, controls);
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
  onMessage: (data: string, controls: ExecutionWebSocketControls) => void,
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
