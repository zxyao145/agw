import type { UserInputRef } from "@/components/message/user-input";
import type { LineComment } from "@/components/file-explorer";

export type AdditionalProperties = Record<string, unknown>;

export const ClaudeCodeMessageType = {
  system: "system",
  assistant: "assistant",
  result: "result",
} as const;

export type ClaudeCodeMessageType =
  (typeof ClaudeCodeMessageType)[keyof typeof ClaudeCodeMessageType];

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

export const DirectoryMode = {
  workingDirectory: "workingDirectory",
  gitAddress: "gitAddress",
} as const;

export type DirectoryMode = (typeof DirectoryMode)[keyof typeof DirectoryMode];

// ============================================================================
// Chat Component Types
// ============================================================================

export interface ChatInputAreaProps {
  isExecuting: boolean;
  hasMessages: boolean;
  onExecute: (value: string) => void;
  onExecuteWithComment: (value: string) => void;
  onInterrupt: () => void;
  onClearSession: () => void;
  onScrollToTop: () => void;
  workingDirectory: string;
  setWorkingDirectory: (value: string) => void;
  gitAddress: string;
  setGitAddress: (value: string) => void;
  directoryMode: DirectoryMode;
  setDirectoryMode: (value: DirectoryMode) => void;
  apiKey: string;
  setApiKey: (value: string) => void;
  apiBaseUrl: string;
  setApiBaseUrl: (value: string) => void;
  permissionMode: string;
  setPermissionMode: (value: string) => void;
  envVars: EnvVar[];
  setEnvVars: (value: EnvVar[]) => void;

  initContent: InitMessageContent | null;
  createArr: (key: string, value: string[] | undefined) => React.ReactNode;

  currentTab: string;
  comments: LineComment[];
  userInputRef?: React.RefObject<UserInputRef | null>;
}

// ============================================================================
// Settings Dialog Component Types
// ============================================================================
export interface EnvVar {
  key: string;
  value: string;
}

export interface SettingsDialogProps {
  workingDirectory: string;
  setWorkingDirectory: (value: string) => void;
  gitAddress: string;
  setGitAddress: (value: string) => void;
  directoryMode: DirectoryMode;
  setDirectoryMode: (value: DirectoryMode) => void;
  apiKey: string;
  setApiKey: (value: string) => void;
  apiBaseUrl: string;
  setApiBaseUrl: (value: string) => void;
  permissionMode: string;
  setPermissionMode: (value: string) => void;
  envVars: EnvVar[];
  setEnvVars: (value: EnvVar[]) => void;
}
