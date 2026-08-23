"use client";

import * as React from "react";
import { LoaderCircle } from "lucide-react";
import { useVirtualizer } from "@tanstack/react-virtual";

import type { ConversationRenderItem } from "@agw/chat-core";
import type { PermissionMode } from "@agw/execution-core";
import {
  Accordion,
  AccordionContent,
  AccordionItem,
  Badge,
  Button,
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyTitle,
  cn,
} from "@agw/components";

import { MessageTrigger } from "../message-trigger";
import { AgentflowCheckpointCard } from "./agentflow-checkpoint-card";
import { HumanGateApproval } from "./human-gate-approval";
import { HumanInteractionPanel } from "./human-interaction-panel";
import { HumanInteractionQuestionResultView } from "./human-interaction-question-result";
import { PresentedMessageComponent } from "./presented-message";
import { ToolState } from "./tool-state";

export type HumanResponseInput = {
  approved: boolean;
  responseText?: string;
  approvalScope?: "once" | "always-tool" | "always-arguments";
  responseData?: unknown;
};

export interface ChatSessionProps {
  items: ConversationRenderItem[];
  scrollElementRef: React.RefObject<HTMLDivElement | null>;
  hasOlderMessages?: boolean;
  isLoadingOlderMessages?: boolean;
  onLoadOlderMessages?: () => void;
  permissionMode?: PermissionMode;
  showCheckpointResume?: boolean;
  checkpointResumeDisabled?: boolean;
  onCheckpointResume?: (occurrenceId: string) => void;
  onHumanResponse?: (response: HumanResponseInput) => void;
}

export function Conversation({
  items,
  scrollElementRef,
  hasOlderMessages = false,
  isLoadingOlderMessages = false,
  onLoadOlderMessages,
  permissionMode,
  showCheckpointResume = false,
  checkpointResumeDisabled = false,
  onCheckpointResume,
  onHumanResponse,
}: ChatSessionProps) {
  const hasHistoryLoader = hasOlderMessages || isLoadingOlderMessages;
  const rowOffset = hasHistoryLoader ? 1 : 0;
  const getItemKey = React.useCallback(
    (index: number) =>
      index === 0 && hasHistoryLoader
        ? "older-message-loader"
        : (items[index - rowOffset]?.key ?? `conversation-row-${index}`),
    [hasHistoryLoader, items, rowOffset],
  );
  const virtualizer = useVirtualizer({
    count: items.length + rowOffset,
    getScrollElement: () => scrollElementRef.current,
    estimateSize: () => 160,
    getItemKey,
    overscan: 6,
  });
  const virtualRows = virtualizer.getVirtualItems();

  if (items.length === 0 && !hasHistoryLoader) {
    return (
      <Empty>
        <EmptyHeader>
          <EmptyTitle>No Message Yet</EmptyTitle>
          <EmptyDescription>There are currently no messages.</EmptyDescription>
        </EmptyHeader>
        <EmptyContent className="flex-row justify-center gap-2" />
      </Empty>
    );
  }

  return (
    <div className="min-h-full w-full flex-1" role="list" aria-label="Conversation messages">
      <div
        className="relative mx-auto w-full max-w-225 pb-36"
        style={{ height: virtualizer.getTotalSize() + 144 }}
      >
        {virtualRows.map((virtualRow) => {
          const isLoader = hasHistoryLoader && virtualRow.index === 0;
          const item = isLoader ? null : items[virtualRow.index - rowOffset];

          return (
            <div
              key={virtualRow.key}
              ref={virtualizer.measureElement}
              data-index={virtualRow.index}
              role="listitem"
              className="absolute top-0 left-0 w-full py-2"
              style={{ transform: `translateY(${virtualRow.start}px)` }}
            >
              {isLoader ? (
                <div className="flex justify-center px-4 py-2" aria-live="polite">
                  <Button
                    type="button"
                    size="sm"
                    variant="ghost"
                    className="rounded-full text-xs text-muted-foreground"
                    disabled={isLoadingOlderMessages}
                    onClick={onLoadOlderMessages}
                  >
                    {isLoadingOlderMessages ? (
                      <LoaderCircle className="size-3.5 animate-spin" />
                    ) : null}
                    {isLoadingOlderMessages ? "Loading earlier messages…" : "Load earlier messages"}
                  </Button>
                </div>
              ) : item ? (
                <ConversationItem
                  item={item}
                  permissionMode={permissionMode}
                  showCheckpointResume={showCheckpointResume}
                  checkpointResumeDisabled={checkpointResumeDisabled}
                  onCheckpointResume={onCheckpointResume}
                  onHumanResponse={onHumanResponse}
                />
              ) : null}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function ConversationItem({
  item,
  permissionMode,
  showCheckpointResume,
  checkpointResumeDisabled,
  onCheckpointResume,
  onHumanResponse,
}: {
  item: ConversationRenderItem;
  permissionMode?: PermissionMode;
  showCheckpointResume: boolean;
  checkpointResumeDisabled: boolean;
  onCheckpointResume?: (occurrenceId: string) => void;
  onHumanResponse?: (response: HumanResponseInput) => void;
}) {
  if (item.type === "checkpoint") {
    return (
      <AgentflowCheckpointCard
        name={item.checkpoint.name}
        showResume={showCheckpointResume}
        available={item.availability?.available === true}
        disabled={checkpointResumeDisabled || !onCheckpointResume}
        onResume={() => onCheckpointResume?.(item.checkpoint.occurrenceId)}
      />
    );
  }

  if (item.type === "human-interaction-result") {
    return (
      <div className="mx-4 max-w-full">
        <HumanInteractionQuestionResultView result={item.result} />
      </div>
    );
  }

  if (item.type === "human-interaction") {
    if (item.request.requestType === "human-interaction") {
      return (
        <div className="mx-4 max-w-full">
          <div className="mb-2 flex items-center gap-2 px-1">
            <Badge variant="secondary" className="text-xs">
              {item.request.toolName ?? "Function"}
            </Badge>
            <span className="text-xs text-muted-foreground">Waiting for your input</span>
          </div>
          <HumanInteractionPanel
            request={{ ...item.request, requestType: "human-interaction" }}
            embedded={item.embedded}
            onSubmit={(responseData) =>
              onHumanResponse?.({ approved: true, approvalScope: "once", responseData })
            }
            onCancel={() => onHumanResponse?.({ approved: false })}
          />
        </div>
      );
    }

    return (
      <div className="mx-4 max-w-full">
        <HumanGateApproval
          request={item.request}
          permissionMode={permissionMode}
          onApprove={(approvalScope, responseText, responseData) =>
            onHumanResponse?.({
              approved: true,
              approvalScope,
              responseText,
              responseData,
            })
          }
          onReject={(responseText) => onHumanResponse?.({ approved: false, responseText })}
        />
      </div>
    );
  }

  if (item.type === "tool-accordion") {
    return (
      <div className="mx-4 max-w-[80%]">
        <Accordion type="single" collapsible className="w-full">
          <AccordionItem value="tool">
            <MessageTrigger className="cursor-pointer py-0">
              <div className="flex flex-2 items-center gap-2">
                <Badge variant="secondary" className="text-xs">
                  {item.toolName}
                </Badge>
              </div>
            </MessageTrigger>
            <AccordionContent>
              <div className="space-y-4">
                {item.messages.map((message) => (
                  <PresentedMessageComponent key={message.identity} message={message} embedded />
                ))}
              </div>
            </AccordionContent>
          </AccordionItem>
        </Accordion>
      </div>
    );
  }

  if (item.type === "tool-state") {
    return (
      <div className="mx-4 max-w-[80%]">
        <ToolState message={item.message} />
      </div>
    );
  }

  const message = item.message;
  return (
    <div
      className={cn(
        "mx-4 max-w-full",
        item.type === "result" && "mt-8 border-t border-dashed pt-4",
      )}
      data-msg-id={message.source.messageId}
    >
      {message.meta ? (
        <div
          className={cn(
            "mb-1 flex max-w-[80%] items-center gap-1.5 text-xs text-muted-foreground",
            message.alignment === "right" && "ml-auto justify-end",
          )}
        >
          {message.meta.name ? (
            <span className="min-w-0 truncate font-medium text-foreground/70">
              {message.meta.name}
            </span>
          ) : null}
          {message.meta.name && message.meta.author ? (
            <span className="shrink-0 text-muted-foreground/60">/</span>
          ) : null}
          {message.meta.author ? (
            <span className="min-w-0 truncate font-mono text-[11px]">{message.meta.author}</span>
          ) : null}
        </div>
      ) : null}
      <PresentedMessageComponent message={message} />
    </div>
  );
}
