"use client";

import { Badge } from "@/components/ui/badge";
import { Accordion, AccordionContent, AccordionItem } from "@/components/ui/accordion";
import { AiMessageComponent, isResultMessage } from "./message";
import { AiMessage, MessageContentType, ProcessedMessageItem } from "@/types";
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyTitle,
} from "@/components/ui/empty";
import { collapseConsecutiveSystemMessages } from "@/lib/chat/ai-message-handlers";
import { cn } from "@/lib/utils";
import { MessageTrigger } from "../MessageTrigger";

export interface ChatSessionProps {
  messages: AiMessage[];
  messagesStartRef?: React.RefObject<HTMLDivElement>;
  messagesEndRef: React.RefObject<HTMLDivElement>;
  processMessages?: (msgs: AiMessage[]) => ProcessedMessageItem[];
  scrollable?: boolean;
}

type MessageMeta = {
  name: string | null;
  author: string | null;
};

const AGENT_NAME_KEYS = ["name", "agentName", "displayName", "agentDisplayName"];

function readStringProperty(message: AiMessage, keys: string[]): string | null {
  const messageRecord = message as unknown as Record<string, unknown>;

  for (const key of keys) {
    const value = message.additionalProperties?.[key] ?? messageRecord[key];
    if (typeof value === "string" && value.trim()) {
      return value.trim();
    }
  }

  return null;
}

function getMessageMeta(message: AiMessage): MessageMeta | null {
  if (isResultMessage(message)) {
    return null;
  }

  const agentAuthor = message.author?.trim() || null;
  if (message.role === "user") {
    return agentAuthor ? { name: null, author: agentAuthor } : null;
  }

  const agentName = readStringProperty(message, AGENT_NAME_KEYS);
  const displayName = agentName && agentName !== agentAuthor ? agentName : null;

  if (!displayName && !agentAuthor) {
    return null;
  }

  return {
    name: displayName,
    author: agentAuthor,
  };
}

const defaultProcessMessages = (msgs: AiMessage[]): ProcessedMessageItem[] => {
  const items: ProcessedMessageItem[] = [];

  // Track which message indices have been processed
  const processedIndices = new Set<number>();
  const msgLength = msgs?.length ?? 0;
  for (let i = 0; i < msgLength; i++) {
    if (processedIndices.has(i)) {
      continue; // Skip already processed messages
    }
    processedIndices.add(i);

    const currentMsg = msgs[i];
    // console.debug("Processing message", i, JSON.stringify(currentMsg));
    const isResult = isResultMessage(currentMsg);

    if (isResult) {
      items.push({
        type: "result",
        message: currentMsg,
      });
      processedIndices.add(i);
      continue;
    }

    if (!currentMsg.author && currentMsg.role !== "system") {
      continue;
    }

    if (!currentMsg.contents || currentMsg.contents.length === 0) {
      continue;
    }

    // Check if current message is a FunctionCall
    const isFunctionCall = currentMsg.contents[0].type === MessageContentType.FunctionCallContent;
    if (isFunctionCall) {
      handleFunctionCall(currentMsg, i);
      continue;
    }

    // Check if it's an orphaned FunctionResult
    const isFunctionResult =
      currentMsg.contents[0].type === MessageContentType.FunctionResultContent;
    if (isFunctionResult) {
      // This FunctionResult wasn't matched to any FunctionCall
      // (either no callId, or FunctionCall hasn't appeared yet, or already processed)
      items.push({
        type: "normal",
        message: currentMsg,
      });
      continue;
    }

    // Normal message (user, assistant, etc.)
    items.push({
      type: "normal",
      message: currentMsg,
    });
    processedIndices.add(i);
  }

  return items;

  function handleFunctionCall(currentMsg: AiMessage, i: number) {
    const callId = currentMsg.contents[0].additionalProperties?.callId as string;

    if (callId) {
      // Find all FunctionResults with matching callId (anywhere in the message list)
      const matchingResults: { msg: AiMessage; index: number }[] = [];

      for (let j = 0; j < msgs.length; j++) {
        if (j === i || processedIndices.has(j)) continue;

        const msg = msgs[j];
        const isFunctionResult =
          msg?.contents?.length === 1 &&
          msg.contents[0].type === MessageContentType.FunctionResultContent;

        if (isFunctionResult) {
          const resultCallId = msg.contents[0].additionalProperties?.callId as string;
          if (resultCallId === callId && msg.streamingScopeId === currentMsg.streamingScopeId) {
            matchingResults.push({ msg, index: j });
          }
        }
      }

      // If we found matching results, create an accordion group
      if (matchingResults.length > 0) {
        const toolName = (currentMsg.contents[0].additionalProperties?.toolName as string) ?? "";
        const groupedMessages = [currentMsg, ...matchingResults.map((r) => r.msg)];

        items.push({
          type: "accordion",
          messages: groupedMessages,
          toolName,
        });

        // Mark all these messages as processed
        processedIndices.add(i);
        matchingResults.forEach((r) => processedIndices.add(r.index));
      } else {
        // FunctionCall without matching results, treat as normal
        items.push({
          type: "normal",
          message: currentMsg,
        });
        processedIndices.add(i);
      }
    } else {
      // FunctionCall without callId, treat as normal
      items.push({
        type: "normal",
        message: currentMsg,
      });
      processedIndices.add(i);
    }
  }
};

export function Conversation({
  messages,
  messagesStartRef,
  messagesEndRef,
  processMessages = defaultProcessMessages,
  scrollable = true,
}: ChatSessionProps) {
  if (!messages || messages.length == 0) {
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

  const visibleMessages = collapseConsecutiveSystemMessages(messages);

  return (
    <div className={cn("min-h-full w-full flex-1", scrollable && "overflow-y-auto")}>
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

        {processMessages(visibleMessages).map((item, index) => {
          // function call / tool use
          if (item.type === "accordion") {
            return (
              <div className="mx-4 max-w-[80%]" key={index}>
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
                        {item.messages.map((msg, msgIndex) => (
                          <AiMessageComponent key={msgIndex} message={msg} />
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

            console.debug("isResult", isResult, "message", item.message);
            return (
              <div
                className={cn(
                  "mx-4 max-w-full",
                  isResult ? "border-t border-dashed pt-4 mt-8" : "",
                )}
                key={index}
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

        <div ref={messagesEndRef} />
      </div>
    </div>
  );
}
