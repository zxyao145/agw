"use client";

import { Button } from "@/components/ui/button";
import { SuggestionItem, UserInput } from "@/components/message/user-input";
import type { ChatInputAreaProps } from "../../types";
import { SettingsDialog } from "./settings-dialog";
import { ChatInfoPopover } from "./chat-info-popover";
import { searchCommand } from "../../lib/search_command";
import { searchFile } from "../../lib/search_file";

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

  const handleSuggestion = (input: string): SuggestionItem[] => {
    input = input.trimStart();
    let value = "";
    if(input.indexOf(' ') < 0){
      value = input;
    }else{
      const textInfo = input.split(' ');
      value = textInfo[textInfo.length - 1]
    }
    if (!value) {
      return [];
    }
    // Extract the search query after the slash
    const query = value.slice(1).trim();
    if (!query) {
      return [];
    }

    if (value.startsWith("/")) {
      return searchCommand(query, initContent?.slashCommands ?? []);
    }
    if (value.startsWith("@")) {
      return searchFile(query);
    }

    return [];
  };

  return (
    <UserInput
      onSuggestion={handleSuggestion}
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
