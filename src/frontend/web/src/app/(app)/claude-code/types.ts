export type AdditionalProperties = Record<string, any>;

export enum ClaudeCodeMessageType {
  system = "system",
  assistant = "assistant",
  result = "result",
}

export interface InitMessageContent {
  claudeCodeVersion: string;
  permissionMode: string;
  model:  string;
  tools: string[];
  slashCommands: string[];
  agents: string[];
  skills: string[];
  plugins: string[];
  mcpServers: string[];
}


export const PermissionMode = {
  default: "default",
  acceptEdits: "acceptEdits",
  plan: "plan",
  bypassPermissions: "bypassPermissions",
} as const;
