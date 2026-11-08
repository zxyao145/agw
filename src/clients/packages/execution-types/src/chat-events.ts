import type { ExecutionMessage } from "./message";

/**
 * 事件所属的 Server、Project 与对话；不属于对话的请求中为 null。
 * The server, project and conversation of an event; null for requests outside a conversation.
 */
export type ChatScope = {
  serverId: string;
  projectId: string | null;
  conversationId: string | null;
};

/** 发起通信的一方。Who started the traffic. */
export type ChatEventOrigin = { kind: "client" } | { kind: "extension"; extensionId: string };

export type ChatChannel = "http" | "execution";

/** ExecutionSession 调用的 hub 方法。The hub methods ExecutionSession calls. */
export type ExecutionHubMethod =
  | "DispatchCommand"
  | "GetExecutionProvider"
  | "GetAgentflowCheckpoints"
  | "FindInProcessExecution"
  | "RecoverInProcessExecution";

export type ChatHttpRequest = {
  method: "GET" | "POST" | "PUT" | "DELETE";
  /** OpenAPI 路径模板。The OpenAPI path template, e.g. `/api/projects/{projectId}/conversations/{conversationId}`. */
  path: string;
  params: { path?: Record<string, unknown>; query?: Record<string, unknown> } | null;
  body: unknown;
};

export type ChatExecutionRequest = {
  method: ExecutionHubMethod;
  /** hub 方法参数；DispatchCommand 的第一个参数是命令对象。The hub method arguments; DispatchCommand's first one is the command. */
  args: readonly unknown[];
};

type ChatRequestPart =
  | { channel: "http"; request: ChatHttpRequest }
  | { channel: "execution"; request: ChatExecutionRequest };

export type ChatEventError = {
  name: string;
  message: string;
  /** HTTP 状态码；hub 调用与网络错误为 null。The HTTP status; null for hub calls and network failures. */
  status: number | null;
  /** 服务端返回的响应体；没有时为 null。The body the server returned; null when none. */
  body: unknown;
};

type ChatEventBase = {
  id: string;
  /** RFC 3339 UTC 时间。RFC 3339 UTC time. */
  time: string;
  scope: ChatScope;
  origin: ChatEventOrigin;
};

export type ChatRequestEvent = ChatEventBase & { kind: "request" } & ChatRequestPart;

export type ChatResponseEvent = ChatEventBase & {
  kind: "response";
  /** 对应的 request 事件 id。The id of the matching request event. */
  requestId: string;
} & ChatRequestPart &
  ({ ok: true; value: unknown } | { ok: false; error: ChatEventError });

/**
 * 消息来源：live 是 ReceiveMessage 推送的消息，经 execution 通道到达；history 是历史分页响应中的消息，经 http 通道到达，
 * requestId 为该页 request 事件的 id。
 * The message source: live is a message pushed by ReceiveMessage over the execution channel; history is a message of a
 * history page response over the http channel, and requestId is that page's request event id.
 */
type ChatMessageSourcePart =
  | { source: "live"; channel: "execution"; requestId: null }
  | { source: "history"; channel: "http"; requestId: string };

export type ChatMessageSource = ChatMessageSourcePart["source"];

export type ChatMessageEvent<TMessage extends ExecutionMessage = ExecutionMessage> =
  ChatEventBase & {
    kind: "message";
    message: TMessage;
  } & ChatMessageSourcePart;

export type ChatEvent<TMessage extends ExecutionMessage = ExecutionMessage> =
  | ChatRequestEvent
  | ChatResponseEvent
  | ChatMessageEvent<TMessage>;

export type ChatEventKind = ChatEvent["kind"];

export type ChatEventFilter = {
  kinds?: readonly ChatEventKind[];
  channels?: readonly ChatChannel[];
  origins?: readonly ChatEventOrigin["kind"][];
  serverId?: string;
  projectId?: string;
  conversationId?: string;
  /** 以下四项只描述消息。The four fields below describe messages only. */
  sources?: readonly ChatMessageSource[];
  roles?: readonly string[];
  messageTypes?: readonly (string | null)[];
  contentTypes?: readonly string[];
};
