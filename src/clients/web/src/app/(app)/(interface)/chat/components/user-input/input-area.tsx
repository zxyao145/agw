"use client";

import * as React from "react";
import { ArrowUp, Eraser, Square } from "lucide-react";

import { QuickTextDialog } from "@/components/task/quick-text-dialog";
import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import { UserInput, type UserInputRef } from "@/components/message/user-input";
import { getTrailingSuggestionTrigger } from "@/components/message/suggestion-trigger";
import type { ChatInputAreaProps } from "../../types";
import { searchCommand } from "../../lib/search_command";
import { searchFile } from "../../lib/search_file";

export function InputArea({
  isExecuting,
  hasMessages,
  onExecute,
  onInterrupt,
  onClearSession,
  onScrollToTop,
  workspace,
  commandSource,
  userInputRef: externalUserInputRef,
}: ChatInputAreaProps) {
  const internalUserInputRef = React.useRef<UserInputRef | null>(null);
  const userInputRef = externalUserInputRef ?? internalUserInputRef;

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

      return searchFile(workspace, trigger.query);
    },
    [commandSource, workspace],
  );

  return (
    <UserInput
      ref={userInputRef}
      isExecuting={isExecuting}
      onExecute={onExecute}
      onStop={onInterrupt}
      onSuggestion={handleSuggestion}
    >
      <UserInput.TopRight>
        <QuickTextDialog onCommandSelect={handleQuickCommand} />
        <Separator orientation="vertical" />
        <Button
          onClick={onClearSession}
          disabled={isExecuting || !hasMessages}
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
      {isExecuting ? (
        <UserInput.Sender>
          <Square size={20} />
        </UserInput.Sender>
      ) : null}
    </UserInput>
  );
}
