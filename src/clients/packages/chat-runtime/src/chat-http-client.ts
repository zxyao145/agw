import {
  ApiError,
  apiDelete,
  apiGet,
  apiPut,
  createUuidV7,
  getApiRuntime,
  type AgwApiClient,
  type AiMessage,
  type ApiResponse,
} from "@agw/api";
import type { AgentSuggestionsResponse } from "@agw/chat-core";
import {
  matchesChatEvent,
  snapshotChatEvent,
  validateChatEventFilter,
  type ChatEvent,
  type ChatEventError,
  type ChatEventFilter,
  type ChatEventOrigin,
  type ChatHttpRequest,
  type ChatScope,
} from "@agw/execution-core";
import {
  clearProjectConversationRecords,
  getConversationTurnInputs,
  getProjectConversationDetails,
  getProjectConversationMessages,
  type ConversationDetails,
  type ConversationHistory,
  type ConversationMessagePage,
  type ConversationMessagePageOptions,
  type ConversationTurnInputSummary,
} from "@agw/projects-core";
import {
  ExecutionSession,
  getExecutionRuntime,
  type ExecutionHubHandlers,
  type ExecutionRuntimeConfig,
  type ExecutionTrafficObserver,
} from "./execution-session";

type ChatApiClient = Pick<AgwApiClient, "apiGet" | "apiPut" | "apiDelete">;

export type ChatHttpClientOptions = {
  /** 对话 HTTP 接口使用的客户端。The client the conversation HTTP endpoints use. */
  api: ChatApiClient;
  /** HTTP 请求发出时的连接身份。The connection identity when an HTTP request is sent. */
  identity: () => ChatConnectionIdentity;
  /** 创建执行会话时读取的 SignalR 配置。The SignalR configuration read when an execution session is created. */
  execution: () => ExecutionRuntimeConfig;
};

/** 一个对话的作用范围。The scope of one conversation. */
export type ChatConversationScope = {
  serverId: string;
  projectId: string;
  conversationId: string;
};

/** 对话详情接口的路径模板。The path template of the conversation details endpoint. */
export const CONVERSATION_DETAILS_PATH = "/api/projects/{projectId}/conversations/{conversationId}";

/**
 * 一次通信所用连接的身份：规范化后的 baseUrl（去掉首尾空白与末尾的 `/`，与 `configureApiRuntime` 相同）与 Bearer token。
 * 只交给进程内的订阅函数，不在事件与副本中。
 * The identity of the connection one piece of traffic used: the normalized baseUrl (trimmed and without trailing `/`, as
 * `configureApiRuntime` does) and the Bearer token. Passed only to in-process listeners, never part of the event or its snapshot.
 */
export type ChatConnectionIdentity = { baseUrl: string; token: string | null };

export type ChatEventListener = (
  event: ChatEvent<AiMessage>,
  connection: ChatConnectionIdentity,
) => void;

type Subscription = { filter: ChatEventFilter; listener: ChatEventListener };

type HttpMethod = ChatHttpRequest["method"];

type HttpRequestOptions = {
  params?: ChatHttpRequest["params"];
  body?: unknown;
};

/** 一次方法调用中最近一个请求的事件 id 与连接身份；历史消息事件使用它们。The event id and identity of a call's latest request; history message events use them. */
type HttpTrace = { requestId: string; connection: ChatConnectionIdentity } | null;

const CLIENT_ORIGIN: ChatEventOrigin = { kind: "client" };

/**
 * 对话与 Agw Server 之间的通信入口：SignalR 执行会话与对话 HTTP 接口都经它发出，每次请求、请求的结果与服务端返回的每条消息都作为通信事件派发给订阅方。
 * The entry point of conversation traffic with the Agw Server: SignalR execution sessions and the conversation HTTP endpoints go through it, and every request, its outcome and every message the server returns are dispatched to subscribers as traffic events.
 */
export class ChatHttpClient {
  private readonly options: ChatHttpClientOptions;
  private readonly subscriptions = new Set<Subscription>();

  public constructor(options: ChatHttpClientOptions) {
    this.options = options;
  }

  /**
   * 订阅通信事件，返回取消订阅的函数；条件不合法时抛出。
   * Subscribes to traffic events and returns the unsubscribe function; throws for an invalid filter.
   */
  public subscribe(filter: ChatEventFilter, listener: ChatEventListener): () => void {
    const problem = validateChatEventFilter(filter);
    if (problem !== null) throw new Error(problem);
    const subscription: Subscription = { filter, listener };
    this.subscriptions.add(subscription);
    return () => {
      this.subscriptions.delete(subscription);
    };
  }

  /** 当前 HTTP 连接身份，即规范化后的 `options.identity()`。The current HTTP connection identity: the normalized `options.identity()`. */
  public getConnectionIdentity(): ChatConnectionIdentity {
    return normalizeIdentity(this.options.identity());
  }

  /**
   * 创建一个对话的执行会话；连接身份取自会话使用的配置，在会话的整个生命周期中不变。extension 的 `origin` 同时是会话写入 SettingCommand 的 extension 身份。
   * Creates the execution session of one conversation; its connection identity comes from the configuration the session uses and stays fixed for the session's lifetime. An extension `origin` is also the extension identity the session writes into its SettingCommand.
   */
  public createExecutionSession(
    scope: ChatConversationScope,
    handlers: ExecutionHubHandlers,
    options: { runtime?: ExecutionRuntimeConfig; origin?: ChatEventOrigin } = {},
  ): ExecutionSession {
    const runtime = options.runtime ?? this.options.execution();
    const origin = options.origin ?? CLIENT_ORIGIN;
    const connection = normalizeIdentity({ baseUrl: runtime.baseUrl, token: runtime.token });
    return new ExecutionSession(
      handlers,
      runtime,
      this.createObserver(scope, origin, connection),
      origin.kind === "extension" ? origin.extensionId : null,
    );
  }

  /**
   * 读取对话详情；`origin` 是通信事件中的发起方，宿主代 extension 发出时传入该 extension。
   * Reads the conversation details; `origin` is the sender in the traffic events, given as the extension when the host sends it on an extension's behalf.
   */
  public getConversation(
    scope: ChatConversationScope,
    signal?: AbortSignal,
    origin: ChatEventOrigin = CLIENT_ORIGIN,
  ): Promise<ConversationDetails> {
    const { client } = this.createApi(scope, origin);
    return getProjectConversationDetails(scope.projectId, scope.conversationId, client, signal);
  }

  /**
   * 读取一页历史；response 事件之后，为 `items` 中的每条消息按顺序派发 history 消息事件。
   * Reads one history page; after its response event, a history message event is dispatched for each message of `items` in order.
   */
  public async getConversationMessages(
    scope: ChatConversationScope,
    options: ConversationMessagePageOptions,
    origin: ChatEventOrigin = CLIENT_ORIGIN,
  ): Promise<ConversationMessagePage> {
    const api = this.createApi(scope, origin);
    const page = await getProjectConversationMessages(
      scope.projectId,
      scope.conversationId,
      options,
      api.client,
    );
    const trace = api.trace();
    if (trace) {
      for (const message of page.items) {
        this.dispatch(
          () => ({
            id: createUuidV7(),
            time: new Date().toISOString(),
            scope,
            origin,
            kind: "message",
            message,
            source: "history",
            channel: "http",
            requestId: trace.requestId,
          }),
          trace.connection,
        );
      }
    }
    return page;
  }

  /**
   * 读取对话详情与全部历史：先读取详情，再按 newer 方向逐页读取，直到没有更多页。
   * Reads the conversation details and its whole history: the details first, then every newer page until none is left.
   */
  public async getConversationHistory(
    scope: ChatConversationScope,
    signal?: AbortSignal,
    origin: ChatEventOrigin = CLIENT_ORIGIN,
  ): Promise<ConversationHistory> {
    const details = await this.getConversation(scope, signal, origin);
    const messages: AiMessage[] = [];
    let cursor: string | null = null;
    while (true) {
      const page = await this.getConversationMessages(
        scope,
        {
          direction: "newer",
          cursor,
          pageSize: 100,
          signal,
        },
        origin,
      );
      messages.push(...page.items);
      if (!page.hasMore || !page.nextCursor) break;
      cursor = page.nextCursor;
    }
    return { ...details, messages };
  }

  public getConversationTurnInputs(
    scope: ChatConversationScope,
    signal?: AbortSignal,
    origin: ChatEventOrigin = CLIENT_ORIGIN,
  ): Promise<ConversationTurnInputSummary[]> {
    return getConversationTurnInputs(
      scope.conversationId,
      signal,
      this.createApi(scope, origin).client,
    );
  }

  public clearConversationRecords(
    scope: ChatConversationScope,
    origin: ChatEventOrigin = CLIENT_ORIGIN,
  ): Promise<boolean> {
    return clearProjectConversationRecords(
      scope.projectId,
      scope.conversationId,
      this.createApi(scope, origin).client,
    );
  }

  public getPermissionCapabilities(
    scope: ChatScope,
    target: { type: "agent" | "agentflow"; id: string },
    signal?: AbortSignal,
    origin: ChatEventOrigin = CLIENT_ORIGIN,
  ): Promise<ApiResponse<"/api/agents/permission-capabilities", "get">> {
    return this.createApi(scope, origin).client.apiGet("/api/agents/permission-capabilities", {
      params: { query: { type: target.type === "agent" ? 0 : 1, id: target.id } },
      signal,
    });
  }

  public async getAgentSuggestions(
    scope: ChatScope,
    query: { projectId?: string; agentId: string },
    signal?: AbortSignal,
    origin: ChatEventOrigin = CLIENT_ORIGIN,
  ): Promise<AgentSuggestionsResponse> {
    return (await this.createApi(scope, origin).client.apiGet("/api/agents/suggestions", {
      params: { query },
      signal,
    })) as AgentSuggestionsResponse;
  }

  /**
   * 为一次方法调用创建包装后的客户端：每个请求发出前派发 request 事件，结果返回后派发 response 事件，事件的发起方为 `origin`。
   * Creates the wrapped client of one method call: a request event is dispatched before each request is sent, and a response event once its outcome returns, both with `origin` as the sender.
   */
  private createApi(
    scope: ChatScope,
    origin: ChatEventOrigin,
  ): { client: ChatApiClient; trace(): HttpTrace } {
    let trace: HttpTrace = null;
    const send = async (
      method: HttpMethod,
      path: string,
      options: HttpRequestOptions | undefined,
      call: () => Promise<unknown>,
    ): Promise<unknown> => {
      const connection = this.getConnectionIdentity();
      const requestId = createUuidV7();
      trace = { requestId, connection };
      const request: ChatHttpRequest = {
        method,
        path,
        params: options?.params ?? null,
        body: options?.body ?? null,
      };
      this.dispatch(
        () => ({
          id: requestId,
          time: new Date().toISOString(),
          scope,
          origin,
          kind: "request",
          channel: "http",
          request,
        }),
        connection,
      );
      let value: unknown;
      try {
        value = await call();
      } catch (error) {
        this.dispatch(
          () => ({
            id: createUuidV7(),
            time: new Date().toISOString(),
            scope,
            origin,
            kind: "response",
            requestId,
            channel: "http",
            request,
            ok: false,
            error: toChatEventError(error),
          }),
          connection,
        );
        throw error;
      }
      this.dispatch(
        () => ({
          id: createUuidV7(),
          time: new Date().toISOString(),
          scope,
          origin,
          kind: "response",
          requestId,
          channel: "http",
          request,
          ok: true,
          value: value ?? null,
        }),
        connection,
      );
      return value;
    };
    const { api } = this.options;
    const client = {
      apiGet: (path: string, options?: HttpRequestOptions) =>
        send("GET", path, options, () => (api.apiGet as UntypedCall)(path, options)),
      apiPut: (path: string, options?: HttpRequestOptions) =>
        send("PUT", path, options, () => (api.apiPut as UntypedCall)(path, options)),
      apiDelete: (path: string, options?: HttpRequestOptions) =>
        send("DELETE", path, options, () => (api.apiDelete as UntypedCall)(path, options)),
    } as unknown as ChatApiClient;
    return { client, trace: () => trace };
  }

  /**
   * 把会话报告的 hub 调用与推送消息转换为 execution 通道的事件。
   * Converts the hub calls and pushed messages a session reports into execution channel events.
   */
  private createObserver(
    scope: ChatScope,
    origin: ChatEventOrigin,
    connection: ChatConnectionIdentity,
  ): ExecutionTrafficObserver {
    return {
      request: (method, args) => {
        const requestId = createUuidV7();
        const request = { method, args };
        this.dispatch(
          () => ({
            id: requestId,
            time: new Date().toISOString(),
            scope,
            origin,
            kind: "request",
            channel: "execution",
            request,
          }),
          connection,
        );
        return (outcome) =>
          this.dispatch(
            () => ({
              id: createUuidV7(),
              time: new Date().toISOString(),
              scope,
              origin,
              kind: "response",
              requestId,
              channel: "execution",
              request,
              ...(outcome.ok
                ? { ok: true as const, value: outcome.value ?? null }
                : { ok: false as const, error: toHubError(outcome.error) }),
            }),
            connection,
          );
      },
      message: (message) =>
        this.dispatch(
          () => ({
            id: createUuidV7(),
            time: new Date().toISOString(),
            scope,
            origin,
            kind: "message",
            message,
            source: "live",
            channel: "execution",
            requestId: null,
          }),
          connection,
        ),
    };
  }

  /**
   * 派发开始时确定匹配的订阅；有匹配时只复制并冻结一次事件。订阅函数的异常从这里继续抛出。
   * Fixes the matching subscriptions when dispatch starts and copies and freezes the event once when any matches. A listener's exception propagates from here.
   */
  private dispatch(
    createEvent: () => ChatEvent<AiMessage>,
    connection: ChatConnectionIdentity,
  ): void {
    if (this.subscriptions.size === 0) return;
    const event = createEvent();
    const matched = [...this.subscriptions].filter((subscription) =>
      matchesChatEvent(subscription.filter, event),
    );
    if (matched.length === 0) return;
    const snapshot = snapshotChatEvent(event);
    for (const subscription of matched) {
      if (this.subscriptions.has(subscription)) subscription.listener(snapshot, connection);
    }
  }
}

type UntypedCall = (path: string, options?: HttpRequestOptions) => Promise<unknown>;

function normalizeIdentity(identity: ChatConnectionIdentity): ChatConnectionIdentity {
  return Object.freeze({
    baseUrl: identity.baseUrl.trim().replace(/\/+$/u, ""),
    token: identity.token,
  });
}

function toChatEventError(error: unknown): ChatEventError {
  if (error instanceof ApiError) {
    return {
      name: error.name,
      message: error.message,
      status: error.status,
      body: error.body ?? null,
    };
  }
  return toHubError(error);
}

function toHubError(error: unknown): ChatEventError {
  return error instanceof Error
    ? { name: error.name, message: error.message, status: null, body: null }
    : { name: "Error", message: String(error), status: null, body: null };
}

/** Web 与 Desktop 的实例，使用全局的 API 与执行配置。The Web and Desktop instance, using the global API and execution configuration. */
export const chatHttpClient = new ChatHttpClient({
  api: { apiGet, apiPut, apiDelete },
  identity: () => {
    const { baseUrl, token } = getApiRuntime();
    return { baseUrl, token };
  },
  execution: getExecutionRuntime,
});
