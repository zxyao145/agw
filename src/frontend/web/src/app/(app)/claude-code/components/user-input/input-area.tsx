"use client";

import { Send } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import type { ChatInputAreaProps } from "../../types";
import { SettingsDialog } from "./settings-dialog";
import { ChatInfoPopover } from "./chat-info-popover";

export function InputArea({
  input,
  setInput,
  isExecuting,
  hasMessages,
  onKeyDown,
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


  // Original chat-style UI
  return (
    <div className="relative">
      <div className="flex mb-2 gap-2 pointer-events-auto">
        <div className="bg-background border rounded-md flex gap-2 items-center p-0">
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
        </div>

        <div className="flex-1" />
        {hasMessages && (
          <Button size="lg" onClick={onClearSession} disabled={isExecuting}>
            Clear Chat
          </Button>
        )}
      </div>

      <div className="relative">
        <div className="flex flex-row gap-0 items-end bg-background border rounded-lg pointer-events-auto">
          <Textarea
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder="Add optional description for your comments..."
            rows={1}
            className="max-h-50 flex-1 resize-none bg-background border-0 shadow-none focus-visible:ring-0 focus-visible:ring-offset-0 "
            disabled={isExecuting}
          />
          {isCommentMode && comments.length > 0 ? (
            <Button className="cursor-pointer m-2" onClick={onExecuteWithComment} disabled={isExecuting}>
              Send {comments.length} comment{comments.length !== 1 ? "s" : ""}
              <span className="ml-2 text-xs opacity-80">Ctrl Enter</span>
            </Button>
          ) : (
            <Button
              className="cursor-pointer m-2"
              onClick={onExecute}
              disabled={!input.trim() || isExecuting}
              size="lg"
            >
              <Send className="w-5 h-5" />
            </Button>
          )}
        </div>
      </div>

      <p className="text-xs text-muted-foreground mt-2">
        Press Enter for new line • Enter/Shift+Enter to send
      </p>
    </div>
  );
}
