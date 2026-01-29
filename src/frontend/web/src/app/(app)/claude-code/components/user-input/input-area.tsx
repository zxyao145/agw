"use client";

import { Button } from "@/components/ui/button";
import { SuggestionItem, UserInput, type UserInputRef } from "@/components/message/user-input";
import type { ChatInputAreaProps } from "../../types";
import { SettingsDialog } from "./settings-dialog";
import { ChatInfoPopover } from "./chat-info-popover";
import { QuickTextDialog, type QuickTextOption } from "./quick-text-dialog";
import { searchCommand } from "../../lib/search_command";
import { searchFile } from "../../lib/search_file";
import { useRef, useMemo } from "react";



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
  userInputRef: externalUserInputRef,
}: ChatInputAreaProps) {
  const internalUserInputRef = useRef<UserInputRef | null>(null);
  const userInputRef = externalUserInputRef ?? internalUserInputRef;

  const quickCommands: QuickTextOption[] = useMemo(() => [
    {
      id: "analyze",
      label: "Analyze Code",
      text: "Please analyze the code in this file and provide insights about:",
      description: "Request code analysis and insights",
    },
    {
      id: "refactor",
      label: "Refactor",
      text: "Please refactor this code to improve:",
      description: "Request code refactoring",
    },
    {
      id: "explain",
      label: "Explain",
      text: "Please explain how this code works:",
      description: "Request code explanation",
    },
    {
      id: "test",
      label: "Write Tests",
      text: "Please write unit tests for this code:",
      description: "Request test generation",
    },
    {
      id: "debug",
      label: "Debug",
      text: "Please help me debug this issue:",
      description: "Request debugging assistance",
    },
    {
      id: "optimize",
      label: "Optimize",
      text: "Please optimize this code for better performance:",
      description: "Request code optimization",
    },
    {
      id: "document",
      label: "Add Docs",
      text: "Please add documentation comments to this code:",
      description: "Request code documentation",
    },
    {
      id: "review",
      label: "Review",
      text: "Please review this code for potential issues:",
      description: "Request code review",
    },
  ], []);

  const handleQuickCommand = (text: string) => {
    userInputRef.current?.insertText(text);
  };

  // Check if we should show the comment-style UI
  const isCommentMode = currentTab === "files" && comments.length > 0;

  const handleOnExecute = (value: string) => {
    if (isCommentMode && onExecuteWithComment) {
      onExecuteWithComment(value);
    } else {
      onExecute(value);
    }
  };

  const handleSuggestion = (input: string): SuggestionItem[] | Promise<SuggestionItem[]> => {
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

    if (value.startsWith("/")) {

      let allCmds = initContent?.slashCommands ?? [];
      allCmds = allCmds.map((x) => (x.startsWith("/") ? x : "/" + x));
      return searchCommand(query, allCmds);
    }
    if (value.startsWith("@")) {
      return searchFile(workingDirectory, query);
    }

    return [];
  };

  return (
    <UserInput
      ref={userInputRef}
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
        <QuickTextDialog
          quickCommands={quickCommands}
          onCommandSelect={handleQuickCommand}
        />
        {/* <Button
          onClick={onClearSession}
          disabled={isExecuting}
          variant="ghost"
          size="sm"
        >
          Clear Chat
        </Button> */}
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
