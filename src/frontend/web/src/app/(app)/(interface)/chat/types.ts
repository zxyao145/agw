"use client";

import type { UserInputRef } from "@/components/message/user-input";

export type ChatTargetType = "agent" | "agentflow";

export type ChatTargetOption = {
  id: string;
  label: string;
  type: ChatTargetType;
};

export interface ChatInputAreaProps {
  isExecuting: boolean;
  hasMessages: boolean;
  onExecute: (value: string) => void;
  onInterrupt: () => void;
  onClearSession: () => void;
  onScrollToTop: () => void;
  userInputRef?: React.RefObject<UserInputRef | null>;
}
