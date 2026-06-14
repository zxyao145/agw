"use client";

import { Badge } from "@/components/ui/badge";
import {
  Accordion,
  AccordionContent,
  AccordionItem,
  AccordionTrigger,
} from "@/components/ui/accordion";
import { AiMessageComponent } from "./message";
import { AiMessage, MessageContentType, ProcessedMessageItem } from "@/types";
import {
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyTitle,
} from "@/components/ui/empty";
import { cn } from "@/lib/utils";

export interface ChatSessionProps {
  taskId?: string | null;
  messages: AiMessage[];
  messagesStartRef?: React.RefObject<HTMLDivElement>;
  messagesEndRef: React.RefObject<HTMLDivElement>;
  processMessages?: (msgs: AiMessage[]) => ProcessedMessageItem[];
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
    // Skip messages without an author (could be system metadata or similar)
    if (!currentMsg.author) {
      continue;
    }

    if (currentMsg.role === "system") {
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

    // const isReasoning = currentMsg.contents[0].type === MessageContentType.TextReasoningContent;
    // if (isReasoning) {
    //   items.push({
    //     type: "accordion",
    //     messages: [currentMsg],
    //     toolName: "Thinking",
    //   });
    //   continue;
    // }

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

          console.log("Checking for matching FunctionResult", isFunctionResult);

        if (isFunctionResult) {
          const resultCallId = msg.contents[0].additionalProperties?.callId as string;
          if (resultCallId === callId) {
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
  taskId,
  messages,
  messagesStartRef,
  messagesEndRef,
  processMessages = defaultProcessMessages,
}: ChatSessionProps) {
  if (!messages || messages.length == 0) {
    return (
      <Empty>
        <EmptyHeader>
          <EmptyTitle>No Message Yet</EmptyTitle>
          <EmptyDescription>
            {taskId
              ? `There are currently no messages in task ${taskId}.`
              : "There are currently no messages."}
          </EmptyDescription>
        </EmptyHeader>
        <EmptyContent className="flex-row justify-center gap-2"></EmptyContent>
      </Empty>
    );
  }

  return (
    <div className="flex-1 flex min-h-full pb-36 max-w-full">
      <div className="flex-1 overflow-y-auto space-y-4">
        {messagesStartRef ? <div ref={messagesStartRef} /> : null}

        {(messages?.length ?? 0) === 0 && (
          <div className="flex items-center justify-center h-40">
            <div className="text-center text-muted-foreground ">
              <p className="text-lg mb-2">No messages yet</p>
              <p className="text-sm">Start a conversation by typing a message below</p>
            </div>
          </div>
        )}

        {processMessages(messages).map((item, index) => {
          if (item.type === "accordion") {
            return (
              <div className="mx-4 max-w-[80%]" key={index}>
                <Accordion type="single" collapsible className="w-full">
                  <AccordionItem value="item-1" className="border rounded-lg px-2 last:border-b">
                    <AccordionTrigger>
                      <div className="flex items-center gap-2">
                        <Badge variant="secondary" className="text-xs">
                          {item.toolName}
                        </Badge>
                      </div>
                    </AccordionTrigger>
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
            const isUserBlock = item.message.role === "user" && !item.message.additionalProperties;
            console.debug("isUser", isUserBlock, "message", item.message);
            return (
              <div
                className={cn("mx-4", isUserBlock ? "max-w-full" : "max-w-[80%]")}
                key={index}
                data-msg-id={item.message.messageId}
              >
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
