"use client";

import { Button } from "@/components/ui/button";
import { UserInput } from "@/components/message/user-input";
import type { ChatInputAreaProps } from "../../types";
import { SettingsDialog } from "./settings-dialog";
import { ChatInfoPopover } from "./chat-info-popover";

export function InputArea({
  isExecuting,
  hasMessages,
  onExecute,
  onExecuteWithComment,
  onClearSession,
  workingDirectory,
  setWorkingDirectory,
  apiKey,
  setApiKey,
  apiBaseUrl,
  setApiBaseUrl,
  permissionMode,
  setPermissionMode,
  envVars,
  setEnvVars,
  initContent,
  createArr,
  currentTab,
  comments,
}: ChatInputAreaProps) {
  // Check if we should show the comment-style UI
  const isCommentMode = currentTab === "files" && comments.length > 0;

  const handleOnExecute = (value: string) => {
    if (isCommentMode && onExecuteWithComment) {
      onExecuteWithComment(value);
    } else {
      onExecute(value);
    }
  };

  return (
    <UserInput isExecuting={isExecuting} onExecute={handleOnExecute}>
      <UserInput.TopLeft>
        <SettingsDialog
          workingDirectory={workingDirectory}
          setWorkingDirectory={setWorkingDirectory}
          apiKey={apiKey}
          setApiKey={setApiKey}
          apiBaseUrl={apiBaseUrl}
          setApiBaseUrl={setApiBaseUrl}
          permissionMode={permissionMode}
          setPermissionMode={setPermissionMode}
          envVars={envVars}
          setEnvVars={setEnvVars}
        />
        <ChatInfoPopover initContent={initContent} createArr={createArr} />
      </UserInput.TopLeft>

      <UserInput.TopRight>
        {hasMessages && (
          <Button onClick={onClearSession} disabled={isExecuting}>
            Clear Chat
          </Button>
        )}
      </UserInput.TopRight>
      {isCommentMode && comments.length > 0 && (
        <UserInput.Sender>
          <>
            Send {comments.length} comment{comments.length !== 1 ? "s" : ""}
            <span className="ml-2 text-xs opacity-80">Ctrl Enter</span>
          </>
        </UserInput.Sender>
      )}
    </UserInput>
  );
}
