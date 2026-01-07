export interface ClaudeCodeMessage {
  type: string;
  content: string;
  model?: string;
  numTurns?: number;
  totalCostUsd?: number;
  isError: boolean;
  errorMessage?: string;
}

export interface ResultMessage {
  type: string;
  content: string;
  model?: string;
  numTurns?: number;
  totalCostUsd?: number;
  isError: boolean;
  errorMessage?: string;
}

export interface AiMessageContent {
  type: string;
  content: string;
}

export interface AiMessage {
  messageId: string;
  author?: string;
  role?: string;
  contents: AiMessageContent[];
}

export interface SystemMessage {
  // system
  session_id?: string;
}

export enum ClaudeCodeMessageType {
  system = "system",
  assistant = "assistant",
  result = "result",
}

export enum MessageContentType {
  TextContent = "TextContent",
  FunctionCallContent = "FunctionCallContent",
  FunctionResultContent = "FunctionResultContent",
}
