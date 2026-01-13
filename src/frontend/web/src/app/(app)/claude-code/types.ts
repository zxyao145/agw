export type AdditionalProperties = Record<string, unknown>;

export const ClaudeCodeMessageType = {
  system: "system",
  assistant: "assistant",
  result: "result",
} as const;

export type ClaudeCodeMessageType = (typeof ClaudeCodeMessageType)[keyof typeof ClaudeCodeMessageType];

export interface InitMessageContent {
  claudeCodeVersion: string;
  permissionMode: PermissionMode;
  model: string;
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

export type PermissionMode = (typeof PermissionMode)[keyof typeof PermissionMode];

