export type AdditionalProperties = Record<string, unknown>;

export interface AiMessageContent {
  type: string;
  content?: unknown;
  additionalProperties?: AdditionalProperties;
}

export interface AiMessage {
  messageId: string;
  author?: string;
  role?: string;
  contents: AiMessageContent[];
  additionalProperties?: AdditionalProperties;
  streamingScopeId?: string;
  // agent error
  type?: string;
}

export type ProcessedMessageItem =
  | { type: "accordion"; messages: AiMessage[]; toolName: string }
  | { type: "normal"; message: AiMessage }
  | { type: "result"; message: AiMessage };

export const MessageContentType = {
  DataContent: "DataContent",
  ErrorContent: "ErrorContent",
  FunctionCallContent: "FunctionCallContent",
  FunctionResultContent: "FunctionResultContent",
  HostedFileContent: "HostedFileContent",
  HostedVectorStoreContent: "HostedVectorStoreContent",
  TextContent: "TextContent",
  TextReasoningContent: "TextReasoningContent",
  UriContent: "UriContent",
  UsageContent: "UsageContent",
} as const;

export type MessageContentType = (typeof MessageContentType)[keyof typeof MessageContentType];
