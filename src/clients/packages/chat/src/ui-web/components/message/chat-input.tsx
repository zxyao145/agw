"use client";

import * as React from "react";
import { ArrowUp, CornerDownRight, Eraser, RotateCcw, Square, X } from "lucide-react";

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
  pendingFileCommentCount: number;
  onClearPendingFileComments: () => void;
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
  pendingFileCommentCount,
  onClearPendingFileComments,
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
      hasAdditionalInput={pendingFileCommentCount > 0}
      onExecute={onExecute}
      onStop={isTransitioning ? undefined : onInterrupt}
      onSuggestion={handleSuggestion}
      placeholder={placeholder}
    >
      {pendingFileCommentCount > 0 ? (
        <UserInput.Context>
          <div className="flex min-h-10 items-center justify-between gap-3 rounded-lg bg-muted px-3 py-2">
            <div className="flex min-w-0 items-center gap-2 text-sm font-medium text-primary">
              <CornerDownRight className="size-4 shrink-0" />
              <span>
                {pendingFileCommentCount} code comment
                {pendingFileCommentCount === 1 ? "" : "s"}
              </span>
            </div>
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              className="size-7 shrink-0 rounded-full text-muted-foreground hover:text-foreground"
              onClick={onClearPendingFileComments}
              disabled={isBusy}
              aria-label="Clear pending code comments"
              title="Clear pending code comments"
            >
              <X className="size-4" />
            </Button>
          </div>
        </UserInput.Context>
      ) : null}
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
