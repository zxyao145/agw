"use client";

import { Button } from "@/components/ui/button";
import { SuggestionItem, UserInput } from "@/components/message/user-input";
import type { ChatInputAreaProps } from "../../types";
import { SettingsDialog } from "./settings-dialog";
import { ChatInfoPopover } from "./chat-info-popover";
import { text } from "stream/consumers";

export function InputArea({
  isExecuting,
  hasMessages,
  onExecute,
  onExecuteWithComment,
  onClearSession,
  workingDirectory,
  setWorkingDirectory,
  gitAddress,
  setGitAddress,
  directoryMode,
  setDirectoryMode,
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

  const onInputChange = (value: string) => {
    if (!value.startsWith("/")) {
      return [];
    }
    const metaArr = value.split("/");
    if (!metaArr || metaArr.length < 2) {
      return [];
    }
    const meta = metaArr[1];
    if (!meta) {
      return [];
    }
    console.log("meta", meta);
    let contents =
      initContent?.slashCommands.filter((x) => x.indexOf(meta) > -1) ?? [];
    if (contents.length > 10) {
      contents = contents.splice(10);
    }
    const suggestions = contents.map((x) => {
      return {
        text: "/" + x,
      };
    });
    console.log("suggestions", suggestions)
    return suggestions;
  };

  return (
    <UserInput
      onSuggestion={onInputChange}
      isExecuting={isExecuting}
      onExecute={handleOnExecute}
    >
      <UserInput.TopLeft>
        <SettingsDialog
          workingDirectory={workingDirectory}
          setWorkingDirectory={setWorkingDirectory}
          gitAddress={gitAddress}
          setGitAddress={setGitAddress}
          directoryMode={directoryMode}
          setDirectoryMode={setDirectoryMode}
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
