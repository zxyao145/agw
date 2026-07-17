export type { ChatTargetOption, ChatTargetType } from "@agw/api";

export interface EnvVar {
  key: string;
  value: string;
}

export interface ChatProjectSettingsStorageValues {
  targetValue?: string | null;
  envVars?: EnvVar[];
}

export interface InitMessageContent {
  claudeCodeVersion: string;
  permissionMode: string;
  model: string;
  tools: string[];
  slashCommands: string[];
  agents: string[];
  skills: string[];
  plugins: string[];
  mcpServers: string[];
}
