"use client";

import * as React from "react";
import { Badge } from "@agw/components";
import { Accordion, AccordionContent, AccordionItem } from "@agw/components";
import { getMessageMeta } from "@agw/chat-core";
import { AiMessageComponent, isResultMessage } from "./message";
import { MessageContentType, type AiMessage } from "@agw/api";
import { processMessages, type ProcessedMessageItem } from "@agw/execution-core";
import { Empty, EmptyContent, EmptyDescription, EmptyHeader, EmptyTitle } from "@agw/components";
import { collapseConsecutiveSystemMessages } from "../../../lib/chat/ai-message-handlers";
import { cn } from "@agw/components";
import { MessageTrigger } from "../message-trigger";
import {
  getAgentflowCheckpointMessage,
  type AgentflowCheckpointAvailability,
  type PendingHumanGate,
} from "../../../services/execution-hub";
import { matchesHumanInteractionCall } from "../../../services/human-interaction-call";
import { getHumanInteractionQuestionResult } from "../../../services/human-interaction";
import { HumanInteractionPanel } from "./human-interaction-panel";
import { HumanInteractionQuestionResultView } from "./human-interaction-question-result";
import { AgentflowCheckpointCard } from "./agentflow-checkpoint-card";

export interface ChatSessionProps {
  messages: AiMessage[];
  messagesStartRef?: React.RefObject<HTMLDivElement>;
  messagesEndRef: React.RefObject<HTMLDivElement>;
  processMessages?: (msgs: AiMessage[]) => ProcessedMessageItem<AiMessage>[];
  scrollable?: boolean;
  pendingHumanInteraction?: (PendingHumanGate & { requestType: "human-interaction" }) | null;
  onHumanInteractionSubmit?: (responseData: unknown) => void;
  onHumanInteractionCancel?: () => void;
  checkpointAvailability?: AgentflowCheckpointAvailability[];
  showCheckpointResume?: boolean;
  checkpointResumeDisabled?: boolean;
  onCheckpointResume?: (occurrenceId: string) => void;
  footer?: React.ReactNode;
}

function getFunctionToolName(message: AiMessage): string | null {
  const functionCall = message.contents.find(
    (content) => content.type === MessageContentType.FunctionCallContent,
  );
  const toolName = functionCall?.additionalProperties?.toolName;
  return typeof toolName === "string" && toolName.trim() ? toolName.trim() : null;
}

const defaultProcessMessages = processMessages;

function getMessageKey(message: AiMessage): string {
  return JSON.stringify([
    message.streamingScopeId ?? null,
    message.messageId,
    message.role ?? null,
    message.author ?? null,
  ]);
}

function getItemBaseKey(item: ProcessedMessageItem<AiMessage>): string {
  if (item.type === "accordion") {
    const callId = item.messages[0]?.contents[0]?.additionalProperties?.callId;
    return `accordion:${getMessageKey(item.messages[0])}:${String(callId ?? item.toolName)}`;
  }

  const contentType = item.message.contents[0]?.type ?? "empty";
  return `${item.type}:${getMessageKey(item.message)}:${contentType}`;
}

function addStableKeys(items: ProcessedMessageItem<AiMessage>[]) {
  const occurrences = new Map<string, number>();
  return items.map((item) => {
    const baseKey = getItemBaseKey(item);
    const occurrence = occurrences.get(baseKey) ?? 0;
    occurrences.set(baseKey, occurrence + 1);
    return { item, key: `${baseKey}:${occurrence}` };
  });
}

export function Conversation({
  messages,
  messagesStartRef,
  messagesEndRef,
  processMessages = defaultProcessMessages,
  scrollable = true,
  pendingHumanInteraction = null,
  onHumanInteractionSubmit,
  onHumanInteractionCancel,
  checkpointAvailability = [],
  showCheckpointResume = false,
  checkpointResumeDisabled = false,
  onCheckpointResume,
  footer,
}: ChatSessionProps) {
  const processedMessages = React.useMemo(
    () => processMessages(collapseConsecutiveSystemMessages(messages)),
    [messages, processMessages],
  );
  const keyedMessages = React.useMemo(() => addStableKeys(processedMessages), [processedMessages]);
  const checkpointsByOccurrence = React.useMemo(
    () => new Map(checkpointAvailability.map((item) => [item.occurrenceId, item])),
    [checkpointAvailability],
  );

  if ((!messages || messages.length == 0) && !footer) {
    return (
      <Empty>
        <EmptyHeader>
          <EmptyTitle>No Message Yet</EmptyTitle>
          <EmptyDescription>There are currently no messages.</EmptyDescription>
        </EmptyHeader>
        <EmptyContent className="flex-row justify-center gap-2"></EmptyContent>
      </Empty>
    );
  }

  let humanInteractionItemIndex = -1;
  if (pendingHumanInteraction && onHumanInteractionSubmit && onHumanInteractionCancel) {
    for (let index = 0; index < processedMessages.length; index += 1) {
      const item = processedMessages[index];
      if (
        item?.type === "normal" &&
        matchesHumanInteractionCall(item.message, pendingHumanInteraction)
      ) {
        humanInteractionItemIndex = index;
      }
    }
  }

  return (
    <div className={cn("min-h-full w-full flex-1", scrollable && "overflow-y-auto agw-scrollbar")}>
      <div className="mx-auto w-full max-w-225 space-y-4 pb-36">
        {messagesStartRef ? <div ref={messagesStartRef} /> : null}

        {(messages?.length ?? 0) === 0 && (
          <div className="flex items-center justify-center h-40">
            <div className="text-center text-muted-foreground ">
              <p className="text-lg mb-2">No messages yet</p>
              <p className="text-sm">Start a conversation by typing a message below</p>
            </div>
          </div>
        )}

        {keyedMessages.map(({ item, key }, index) => {
          const checkpoint =
            item.type === "normal" ? getAgentflowCheckpointMessage(item.message) : null;
          if (checkpoint) {
            const availability = checkpointsByOccurrence.get(checkpoint.occurrenceId);
            return (
              <AgentflowCheckpointCard
                key={key}
                name={checkpoint.name}
                showResume={showCheckpointResume}
                available={availability?.available === true}
                disabled={checkpointResumeDisabled || !onCheckpointResume}
                onResume={() => onCheckpointResume?.(checkpoint.occurrenceId)}
              />
            );
          }

          if (
            index === humanInteractionItemIndex &&
            item.type === "normal" &&
            pendingHumanInteraction &&
            onHumanInteractionSubmit &&
            onHumanInteractionCancel
          ) {
            const toolName = pendingHumanInteraction.toolName ?? getFunctionToolName(item.message);
            return (
              <div
                className="mx-4 max-w-full"
                key={key}
                data-msg-id={item.message.messageId}
                data-function-call-id={pendingHumanInteraction.callId}
              >
                <div className="mb-2 flex items-center gap-2 px-1">
                  <Badge variant="secondary" className="text-xs">
                    {toolName ?? "Function"}
                  </Badge>
                  <span className="text-xs text-muted-foreground">Waiting for your input</span>
                </div>
                <HumanInteractionPanel
                  request={pendingHumanInteraction}
                  embedded
                  onSubmit={onHumanInteractionSubmit}
                  onCancel={onHumanInteractionCancel}
                />
              </div>
            );
          }

          // function call / tool use
          if (item.type === "accordion") {
            const questionResult =
              item.toolName === "ask_user_question"
                ? getHumanInteractionQuestionResult(item.messages)
                : null;
            if (questionResult) {
              return (
                <div className="mx-4 max-w-[90%]" key={key}>
                  <HumanInteractionQuestionResultView result={questionResult} />
                </div>
              );
            }

            return (
              <div className="mx-4 max-w-[80%]" key={key}>
                <Accordion type="single" collapsible className="w-full">
                  <AccordionItem value="item-1">
                    <MessageTrigger className="py-0 cursor-pointer">
                      <div className="flex flex-2 items-center gap-2">
                        <Badge variant="secondary" className="text-xs">
                          {item.toolName}
                        </Badge>
                      </div>
                    </MessageTrigger>
                    <AccordionContent>
                      <div className="space-y-4">
                        {item.messages.map((msg) => (
                          <AiMessageComponent key={getMessageKey(msg)} message={msg} />
                        ))}
                      </div>
                    </AccordionContent>
                  </AccordionItem>
                </Accordion>
              </div>
            );
          } else {
            const isResult = isResultMessage(item.message);
            const isUserMessage = item.message.role === "user";
            const messageMeta = getMessageMeta(item.message);

            return (
              <div
                className={cn(
                  "mx-4 max-w-full",
                  isResult ? "border-t border-dashed pt-4 mt-8" : "",
                )}
                key={key}
                data-msg-id={item.message.messageId}
              >
                {messageMeta ? (
                  <div
                    className={cn(
                      "mb-1 flex max-w-[80%] items-center gap-1.5 text-xs text-muted-foreground",
                      isUserMessage ? "ml-auto justify-end" : "",
                    )}
                  >
                    {messageMeta.name ? (
                      <span className="min-w-0 truncate font-medium text-foreground/70">
                        {messageMeta.name}
                      </span>
                    ) : null}
                    {messageMeta.name && messageMeta.author ? (
                      <span className="shrink-0 text-muted-foreground/60">/</span>
                    ) : null}
                    {messageMeta.author ? (
                      <span className="min-w-0 truncate font-mono text-[11px]">
                        {messageMeta.author}
                      </span>
                    ) : null}
                  </div>
                ) : null}
                <AiMessageComponent message={item.message} />
              </div>
            );
          }
        })}

        {footer}
        <div ref={messagesEndRef} />
      </div>
    </div>
  );
}
