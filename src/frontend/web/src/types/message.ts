export type AdditionalProperties = Record<string, any>;
export interface AiMessageContent {
  type: string;
  content: string;
  additionalProperties?: AdditionalProperties;
}

export interface AiMessage {
  messageId: string;
  author?: string;
  role?: string;
  contents: AiMessageContent[];
  additionalProperties?: AdditionalProperties;
}


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

export type MessageContentTypes =
  (typeof MessageContentType)[keyof typeof MessageContentType];
