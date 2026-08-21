/** 与具体客户端无关的结构化消息形状。 */

export type AdditionalProperties = Record<string, unknown>;

export interface ExecutionMessageContent {
  type: string;
  content?: unknown;
  additionalProperties?: AdditionalProperties;
}

export interface ExecutionMessage {
  messageId: string;
  author?: string | null;
  role?: string;
  contents: ExecutionMessageContent[];
  additionalProperties?: AdditionalProperties | null;
  streamingScopeId?: string | null;
}
