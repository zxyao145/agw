"use client";

import type { UserInputRef } from "@/components/message/user-input";

export type ChatTargetType = "agent" | "agentflow";

export type ChatTargetOption = {
  id: string;
  label: string;
  type: ChatTargetType;
};

export interface EnvVar {
  key: string;
  value: string;
}

export interface ChatProjectSettingsStorageValues {
  targetValue?: string | null;
  workspace?: string;
  envVars?: EnvVar[];
  extraSettingText?: string;
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

export interface ChatInputAreaProps {
  isExecuting: boolean;
  hasMessages: boolean;
  onExecute: (value: string) => void;
  onInterrupt: () => void;
  onClearSession: () => void;
  onScrollToTop: () => void;
  userInputRef?: React.RefObject<UserInputRef | null>;
}
