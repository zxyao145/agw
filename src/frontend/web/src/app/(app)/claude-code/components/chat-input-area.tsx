"use client";

import { Send } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import type { ChatInputAreaProps } from "../types";
import { SettingsDialog } from "./settings-dialog";
import { ChatInfoPopover } from "./chat-info-popover";

export function ChatInputArea({
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
  initContent,
  createArr,
}: ChatInputAreaProps) {
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
          />
          <ChatInfoPopover initContent={initContent} createArr={createArr} />
        </div>

        <div className="flex-1" />

        <Button onClick={onExecuteWithComment}>Send comment</Button>
      </div>

      <div className="flex gap-2 items-end bg-background pointer-events-auto">
        <Textarea
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={onKeyDown}
          placeholder="Type your message... (Shift+Enter for new line)"
          rows={3}
          className="flex-1 resize-none bg-background"
          disabled={isExecuting}
        />
        <Button
          onClick={onExecute}
          disabled={!input.trim() || isExecuting}
          size="lg"
        >
          <Send className="w-5 h-5" />
        </Button>
        {hasMessages && (
          <Button
            variant="outline"
            size="lg"
            onClick={onClearSession}
            disabled={isExecuting}
          >
            Clear Chat
          </Button>
        )}
      </div>

      <p className="text-xs text-muted-foreground mt-2">
        Press Enter to send • Shift+Enter for new line
      </p>
    </div>
  );
}
