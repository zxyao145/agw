"use client";

import * as React from "react";
import {
  ArrowDown,
  ArrowUp,
  CornerDownRight,
  Eraser,
  LoaderCircle,
  RotateCcw,
  Square,
  X,
} from "lucide-react";
import { toast } from "sonner";

import { QuickTextDialog } from "@agw/projects";
import { Button } from "@agw/components";
import { Separator } from "@agw/components";
import { resolveInputSuggestions, type CommandSource } from "@agw/chat-core";
import { searchFile } from "../../../lib/chat/search-file";
import {
  createImageAttachments,
  type ChatImageAttachment,
  validateImageFiles,
} from "../../../lib/chat/image-attachments";
import type { AgentMode, PermissionMode } from "../../../services/execution-hub";
import { ChatInputToolbar } from "./chat-input-toolbar";
import { UserInput, type UserInputRef } from "./user-input";

interface ChatInputProps {
  isExecuting: boolean;
  isTransitioning: boolean;
  isLoadingHistory: boolean;
  hasMessages: boolean;
  onExecute: (value: string, imageAttachments: readonly ChatImageAttachment[]) => void;
  onInterrupt: () => void;
  onClearSession: () => void;
  onScrollToBottom: () => void;
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
  isLoadingHistory,
  hasMessages,
  onExecute,
  onInterrupt,
  onClearSession,
  onScrollToBottom,
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
  const [imageAttachments, setImageAttachments] = React.useState<ChatImageAttachment[]>([]);
  const [isReadingImages, setIsReadingImages] = React.useState(false);
  const isBusy = isExecuting || isTransitioning;

  const handleQuickCommand = (text: string) => {
    userInputRef.current?.insertText(text);
  };

  const handlePaste = React.useCallback(
    async (event: React.ClipboardEvent<HTMLTextAreaElement>) => {
      const clipboardFiles = Array.from(event.clipboardData.items)
        .filter((item) => item.kind === "file")
        .map((item) => item.getAsFile())
        .filter((file): file is File => file !== null);
      const imageFiles = clipboardFiles.filter((file) => file.type.startsWith("image/"));
      if (imageFiles.length === 0) {
        return;
      }

      event.preventDefault();
      if (isReadingImages) {
        toast.error("Please wait for the pasted images to finish loading.");
        return;
      }

      const validationError = validateImageFiles(imageFiles, imageAttachments);
      if (validationError) {
        toast.error(validationError);
        return;
      }

      setIsReadingImages(true);
      try {
        const attachments = await createImageAttachments(imageFiles);
        setImageAttachments((current) => [...current, ...attachments]);
      } catch (error) {
        toast.error(
          error instanceof Error ? error.message : "The pasted images could not be read.",
        );
      } finally {
        setIsReadingImages(false);
      }
    },
    [imageAttachments, isReadingImages],
  );

  const handleRemoveImage = React.useCallback((attachmentId: string) => {
    setImageAttachments((current) =>
      current.filter((attachment) => attachment.id !== attachmentId),
    );
  }, []);

  const handleExecute = React.useCallback(
    (value: string) => {
      onExecute(value, imageAttachments);
      setImageAttachments([]);
    },
    [imageAttachments, onExecute],
  );

  const handleSuggestion = React.useCallback(
    (input: string, caretIndex: number) =>
      resolveInputSuggestions(input, caretIndex, commandSource, (keyword) =>
        searchFile(projectId, keyword),
      ),
    [commandSource, projectId],
  );

  return (
    <UserInput
      ref={userInputRef}
      isExecuting={isBusy}
      isSubmitDisabled={isReadingImages}
      hasAdditionalInput={pendingFileCommentCount > 0 || imageAttachments.length > 0}
      onExecute={handleExecute}
      onStop={isTransitioning ? undefined : onInterrupt}
      onPaste={handlePaste}
      onSuggestion={handleSuggestion}
      placeholder={placeholder}
    >
      {pendingFileCommentCount > 0 || imageAttachments.length > 0 ? (
        <UserInput.Context>
          <div className="space-y-2">
            {imageAttachments.length > 0 ? (
              <ul
                className="agw-scrollbar flex gap-2 overflow-x-auto pb-1"
                aria-label="Pasted images"
              >
                {imageAttachments.map((attachment) => (
                  <li
                    key={attachment.id}
                    className="group relative size-20 shrink-0 overflow-hidden rounded-xl border bg-muted shadow-xs"
                  >
                    <img
                      src={attachment.dataUrl}
                      alt={attachment.name}
                      className="size-full object-cover"
                    />
                    <Button
                      type="button"
                      variant="secondary"
                      size="icon-sm"
                      className="absolute right-1 top-1 size-6 rounded-full border border-white/20 bg-black/75 text-white shadow-sm hover:bg-black"
                      onClick={() => handleRemoveImage(attachment.id)}
                      disabled={isBusy || isReadingImages}
                      aria-label={`Remove ${attachment.name}`}
                      title={`Remove ${attachment.name}`}
                    >
                      <X className="size-3.5" />
                    </Button>
                  </li>
                ))}
              </ul>
            ) : null}
            {pendingFileCommentCount > 0 ? (
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
            ) : null}
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
        <Button
          className="has-[>svg]:pl-2"
          onClick={onScrollToBottom}
          disabled={!hasMessages || isLoadingHistory}
          title="Go to latest message"
          aria-label="Go to latest message"
          variant="ghost"
          size="sm"
        >
          <ArrowDown width={16} />
        </Button>
        <Separator className="data-[orientation=vertical]:h-[60%]" orientation="vertical" />
        <Button
          className="has-[>svg]:pr-2"
          onClick={onScrollToTop}
          disabled={!hasMessages || isLoadingHistory}
          title={isLoadingHistory ? "Loading complete conversation history" : "Go to first message"}
          aria-label={
            isLoadingHistory ? "Loading complete conversation history" : "Go to first message"
          }
          variant="ghost"
          size="sm"
        >
          {isLoadingHistory ? (
            <LoaderCircle className="animate-spin" width={16} />
          ) : (
            <ArrowUp width={16} />
          )}
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
