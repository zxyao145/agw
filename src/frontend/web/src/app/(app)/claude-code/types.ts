export interface ClaudeCodeMessage {
  type: string;
  content: string;
  model?: string;
  numTurns?: number;
  totalCostUsd?: number;
  isError: boolean;
  errorMessage?: string;


}

export interface SystemMessage{
  // system
  session_id?: string;
}

export enum ClaudeCodeMessageType {
  system = "system",
  assistant = "assistant",
  result = "result",
}
