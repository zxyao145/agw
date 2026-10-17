import { createParser } from "eventsource-parser";
import type { A2AStreamMethod } from "@desktop/shared/extension-api";
import { createExtensionError } from "@desktop/shared/extension-rpc";
import type { ConnectionIdentity, ContextService } from "./context-service";

/** 宿主放行的 A2A 方法；推送通知配置相关的方法被拒绝。A2A methods the host allows; push-notification config methods are refused. */
export const ALLOWED_A2A_METHODS: ReadonlySet<string> = new Set([
  "SendMessage",
  "GetTask",
  "ListTasks",
  "CancelTask",
  "GetExtendedAgentCard",
  "SendStreamingMessage",
  "SubscribeToTask",
]);

const STREAM_METHODS: ReadonlySet<string> = new Set<A2AStreamMethod>([
  "SendStreamingMessage",
  "SubscribeToTask",
]);

type JsonRpcResponse = {
  result?: unknown;
  error?: { code: number; message: string };
};

function headers(identity: ConnectionIdentity, accept: string): Record<string, string> {
  return {
    Accept: accept,
    "A2A-Version": "1.0",
    ...(identity.token ? { Authorization: `Bearer ${identity.token}` } : {}),
  };
}

function agentRoute(identity: ConnectionIdentity, agentName: string): string {
  return `${identity.baseUrl}/a2a/${encodeURIComponent(agentName)}`;
}

async function readJson(response: Response): Promise<unknown> {
  if (!response.ok) {
    throw createExtensionError(
      "ERR_SERVER",
      `A2A request failed with ${response.status} ${response.statusText}.`,
    );
  }
  return response.json() as Promise<unknown>;
}

function assertMethod(method: string): void {
  if (!ALLOWED_A2A_METHODS.has(method)) {
    throw createExtensionError(
      "ERR_UNSUPPORTED_OPERATION",
      `The A2A method ${method} is not available to extensions.`,
    );
  }
}

function rpcResult(body: JsonRpcResponse): unknown {
  if (body.error) {
    throw createExtensionError("ERR_A2A_RPC", `${body.error.code}: ${body.error.message}`);
  }
  return body.result;
}

/** 用当前连接的 baseUrl 与 Bearer token 调用 A2A；token 不交给 extension。Calls A2A with the current connection; the token stays in the host. */
export class A2AService {
  public constructor(private readonly context: ContextService) {}

  public listAgents(signal?: AbortSignal): Promise<unknown[]> {
    return this.context.request(async (requestSignal, identity) => {
      const response = await fetch(`${identity.baseUrl}/.well-known/agents.json`, {
        headers: headers(identity, "application/json"),
        credentials: "omit",
        signal: requestSignal,
      });
      return (await readJson(response)) as unknown[];
    }, signal);
  }

  public getAgentCard(agentName: string, signal?: AbortSignal): Promise<unknown> {
    return this.context.request(async (requestSignal, identity) => {
      const response = await fetch(
        `${agentRoute(identity, agentName)}/.well-known/agent-card.json`,
        {
          headers: headers(identity, "application/json"),
          credentials: "omit",
          signal: requestSignal,
        },
      );
      return readJson(response);
    }, signal);
  }

  public request(
    agentName: string,
    method: string,
    params: unknown,
    signal?: AbortSignal,
  ): Promise<unknown> {
    assertMethod(method);
    if (STREAM_METHODS.has(method)) {
      throw createExtensionError("ERR_UNSUPPORTED_OPERATION", `Use a2a.stream for ${method}.`);
    }
    return this.context.request(async (requestSignal, identity) => {
      const response = await fetch(agentRoute(identity, agentName), {
        method: "POST",
        headers: { ...headers(identity, "application/json"), "Content-Type": "application/json" },
        body: JSON.stringify({
          jsonrpc: "2.0",
          id: crypto.randomUUID(),
          method,
          params: params ?? {},
        }),
        credentials: "omit",
        signal: requestSignal,
      });
      return rpcResult((await readJson(response)) as JsonRpcResponse);
    }, signal);
  }

  /** 解析 SSE，每个事件的 JSON 交给 `listener`，流结束时完成。Parses SSE, passing each event's JSON to `listener`. */
  public stream(
    agentName: string,
    method: string,
    params: unknown,
    listener: (event: unknown) => void,
    signal?: AbortSignal,
  ): Promise<null> {
    assertMethod(method);
    if (!STREAM_METHODS.has(method)) {
      throw createExtensionError(
        "ERR_UNSUPPORTED_OPERATION",
        `${method} is not a streaming A2A method.`,
      );
    }
    return this.context.request(async (requestSignal, identity) => {
      const response = await fetch(agentRoute(identity, agentName), {
        method: "POST",
        headers: { ...headers(identity, "text/event-stream"), "Content-Type": "application/json" },
        body: JSON.stringify({
          jsonrpc: "2.0",
          id: crypto.randomUUID(),
          method,
          params: params ?? {},
        }),
        credentials: "omit",
        signal: requestSignal,
      });
      if (!response.ok || !response.body) {
        throw createExtensionError(
          "ERR_SERVER",
          `A2A stream failed with ${response.status} ${response.statusText}.`,
        );
      }
      if (!(response.headers.get("content-type") ?? "").includes("text/event-stream")) {
        listener(rpcResult((await response.json()) as JsonRpcResponse));
        return null;
      }
      const parser = createParser({
        onEvent: (event) => listener(JSON.parse(event.data) as unknown),
      });
      const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        parser.feed(value);
      }
      return null;
    }, signal);
  }
}
