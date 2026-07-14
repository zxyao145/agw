"use client";

import type { UserInputRef } from "@/components/message/user-input";
import type { CommandSource } from "./lib/search_command";
export type { ChatTargetOption, ChatTargetType } from "@/types/chat-target";

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

export interface ChatInputAreaProps {
  isExecuting: boolean;
  hasMessages: boolean;
  onExecute: (value: string) => void;
  onInterrupt: () => void;
  onClearSession: () => void;
  onScrollToTop: () => void;
  workspace: string;
  commandSource: CommandSource;
  userInputRef?: React.RefObject<UserInputRef | null>;
}
