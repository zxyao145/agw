"use client";

import * as React from "react";
import { ArrowUp, Eraser, Square } from "lucide-react";

import { QuickTextDialog } from "@/components/task/quick-text-dialog";
import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import { getTrailingSuggestionTrigger } from "@/components/message/suggestion-trigger";
import { UserInput, type UserInputRef } from "@/components/message/user-input";
import { searchCommand, type CommandSource } from "@/lib/chat/search-command";
import { searchFile } from "@/lib/chat/search-file";

interface ChatInputProps {
  isExecuting: boolean;
  isTransitioning: boolean;
  hasMessages: boolean;
  onExecute: (value: string) => void;
  onInterrupt: () => void;
  onClearSession: () => void;
  onScrollToTop: () => void;
  projectId: string | null;
  commandSource: CommandSource;
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
  projectId,
  commandSource,
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
    (input: string) => {
      const trigger = getTrailingSuggestionTrigger(input);
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
      <UserInput.TopRight>
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
