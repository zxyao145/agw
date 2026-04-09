"use client";

import * as React from "react";
import { ArrowUp, Eraser, Square } from "lucide-react";

import { QuickTextDialog } from "@/components/task/quick-text-dialog";
import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import { UserInput, type UserInputRef } from "@/components/message/user-input";
import type { ChatInputAreaProps } from "../../types";

export function InputArea({
  isExecuting,
  hasMessages,
  onExecute,
  onInterrupt,
  onClearSession,
  onScrollToTop,
  userInputRef: externalUserInputRef,
}: ChatInputAreaProps) {
  const internalUserInputRef = React.useRef<UserInputRef | null>(null);
  const userInputRef = externalUserInputRef ?? internalUserInputRef;

  const handleQuickCommand = (text: string) => {
    userInputRef.current?.insertText(text);
  };

  return (
    <UserInput
      ref={userInputRef}
      isExecuting={isExecuting}
      onExecute={onExecute}
      onStop={onInterrupt}
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
