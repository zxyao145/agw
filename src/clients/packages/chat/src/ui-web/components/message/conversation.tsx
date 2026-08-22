"use client";

import * as React from "react";

import type { ConversationRenderItem } from "@agw/chat-core";
import type { PermissionMode } from "@agw/execution-core";
import {
  Accordion,
  AccordionContent,
  AccordionItem,
  Badge,
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
  messagesStartRef?: React.RefObject<HTMLDivElement>;
  messagesEndRef: React.RefObject<HTMLDivElement>;
  scrollable?: boolean;
  permissionMode?: PermissionMode;
  showCheckpointResume?: boolean;
  checkpointResumeDisabled?: boolean;
  onCheckpointResume?: (occurrenceId: string) => void;
  onHumanResponse?: (response: HumanResponseInput) => void;
}

export function Conversation({
  items,
  messagesStartRef,
  messagesEndRef,
  scrollable = true,
  permissionMode,
  showCheckpointResume = false,
  checkpointResumeDisabled = false,
  onCheckpointResume,
  onHumanResponse,
}: ChatSessionProps) {
  if (items.length === 0) {
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
    <div className={cn("min-h-full w-full flex-1", scrollable && "overflow-y-auto agw-scrollbar")}>
      <div className="mx-auto w-full max-w-225 space-y-4 pb-36">
        {messagesStartRef ? <div ref={messagesStartRef} /> : null}
        {items.map((item) => {
          if (item.type === "checkpoint") {
            return (
              <AgentflowCheckpointCard
                key={item.key}
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
              <div className="mx-4 max-w-full" key={item.key}>
                <HumanInteractionQuestionResultView result={item.result} />
              </div>
            );
          }

          if (item.type === "human-interaction") {
            if (item.request.requestType === "human-interaction") {
              return (
                <div className="mx-4 max-w-full" key={item.key}>
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
              <div className="mx-4 max-w-full" key={item.key}>
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
              <div className="mx-4 max-w-[80%]" key={item.key}>
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
                          <PresentedMessageComponent
                            key={message.identity}
                            message={message}
                            embedded
                          />
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
              <div className="mx-4 max-w-[80%]" key={item.key}>
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
              key={item.key}
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
                    <span className="min-w-0 truncate font-mono text-[11px]">
                      {message.meta.author}
                    </span>
                  ) : null}
                </div>
              ) : null}
              <PresentedMessageComponent message={message} />
            </div>
          );
        })}
        <div ref={messagesEndRef} />
      </div>
    </div>
  );
}
