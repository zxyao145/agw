"use client";

import * as React from "react";
import { ArrowUp, Eraser, RotateCcw, Square } from "lucide-react";

import { QuickTextDialog } from "@agw/projects";
import { Button } from "@agw/components";
import { Separator } from "@agw/components";
import { searchCommand, type CommandSource } from "../../../lib/chat/search-command";
import { searchFile } from "../../../lib/chat/search-file";
import type { AgentMode, PermissionMode } from "../../../services/execution-hub";
import { ChatInputToolbar } from "./chat-input-toolbar";
import { getSuggestionTrigger } from "./suggestion-trigger";
import { UserInput, type UserInputRef } from "./user-input";

interface ChatInputProps {
  isExecuting: boolean;
  isTransitioning: boolean;
  hasMessages: boolean;
  onExecute: (value: string) => void;
  onInterrupt: () => void;
  onClearSession: () => void;
  onScrollToTop: () => void;
  showResume: boolean;
  canResume: boolean;
  onResume: () => void;
  projectId: string | null;
  commandSource: CommandSource;
  permissionMode: PermissionMode;
  agentMode: AgentMode;
  onPermissionModeChange: (mode: PermissionMode) => void;
  onAgentModeChange: (mode: AgentMode) => void;
  placeholder?: string;
  userInputRef?: React.RefObject<UserInputRef | null>;
}

export function ChatInput({
  isExecuting,
  isTransitioning,
  hasMessages,
  onExecute,
  onInterrupt,
  onClearSession,
  onScrollToTop,
  showResume,
  canResume,
  onResume,
  projectId,
  commandSource,
  permissionMode,
  agentMode,
  onPermissionModeChange,
  onAgentModeChange,
  placeholder,
  userInputRef: externalUserInputRef,
}: ChatInputProps) {
  const internalUserInputRef = React.useRef<UserInputRef | null>(null);
  const userInputRef = externalUserInputRef ?? internalUserInputRef;
  const isBusy = isExecuting || isTransitioning;

  const handleQuickCommand = (text: string) => {
    userInputRef.current?.insertText(text);
  };

  const handleSuggestion = React.useCallback(
    (input: string, caretIndex: number) => {
      const trigger = getSuggestionTrigger(input, caretIndex);
      if (!trigger) {
        return [];
      }

      if (trigger.type === "command") {
        return searchCommand(trigger.query, commandSource);
      }

      return searchFile(projectId, trigger.query);
    },
    [commandSource, projectId],
  );

  return (
    <UserInput
      ref={userInputRef}
      isExecuting={isBusy}
      onExecute={onExecute}
      onStop={isTransitioning ? undefined : onInterrupt}
      onSuggestion={handleSuggestion}
      placeholder={placeholder}
    >
      <UserInput.BottomLeft>
        <ChatInputToolbar
          commandSource={commandSource}
          isExecuting={isExecuting}
          isTransitioning={isTransitioning}
          permissionMode={permissionMode}
          agentMode={agentMode}
          onCommandSelect={handleQuickCommand}
          onPermissionModeChange={onPermissionModeChange}
          onAgentModeChange={onAgentModeChange}
        />
      </UserInput.BottomLeft>
      <UserInput.TopRight>
        {showResume ? (
          <>
            <Button
              type="button"
              onClick={onResume}
              disabled={!canResume}
              variant="ghost"
              size="sm"
              title={canResume ? "Resume from the latest checkpoint" : "No checkpoint available"}
            >
              <RotateCcw width={16} />
              Resume
            </Button>
            <Separator orientation="vertical" />
          </>
        ) : null}
        <QuickTextDialog onCommandSelect={handleQuickCommand} />
        <Separator orientation="vertical" />
        <Button
          onClick={onClearSession}
          disabled={isBusy || !hasMessages}
          variant="ghost"
          size="sm"
        >
          <Eraser width={16} />
        </Button>
        <Separator orientation="vertical" />
        <Button onClick={onScrollToTop} variant="ghost" size="sm">
          <ArrowUp width={16} />
        </Button>
      </UserInput.TopRight>
      {isExecuting && !isTransitioning ? (
        <UserInput.Sender>
          <Square size={20} />
        </UserInput.Sender>
      ) : null}
    </UserInput>
  );
}
