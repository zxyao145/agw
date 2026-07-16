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
  type FragmentType = "normal" | "result" | "function-call" | "function-result";
  type MessageFragment = {
    type: FragmentType;
    message: AiMessage;
    groupKey: string | null;
    toolName: string;
  };
  type ToolGroup = {
    calls: MessageFragment[];
    results: MessageFragment[];
  };

  const fragments: MessageFragment[] = [];

  for (const message of msgs) {
    if (isResultMessage(message)) {
      fragments.push({ type: "result", message, groupKey: null, toolName: "" });
      continue;
    }

    if ((!message.author && message.role !== "system") || message.contents.length === 0) {
      continue;
    }

    let normalContents: AiMessage["contents"] = [];
    const flushNormalContents = () => {
      if (normalContents.length === 0) {
        return;
      }

      fragments.push({
        type: "normal",
        message: { ...message, contents: normalContents },
        groupKey: null,
        toolName: "",
      });
      normalContents = [];
    };

    for (const content of message.contents) {
      const isFunctionCall = content.type === MessageContentType.FunctionCallContent;
      const isFunctionResult = content.type === MessageContentType.FunctionResultContent;
      if (!isFunctionCall && !isFunctionResult) {
        normalContents.push(content);
        continue;
      }

      flushNormalContents();
      const callId = content.additionalProperties?.callId;
      const groupKey =
        typeof callId === "string" && callId.length > 0
          ? JSON.stringify([message.streamingScopeId ?? null, callId])
          : null;
      const toolName = content.additionalProperties?.toolName;
      fragments.push({
        type: isFunctionCall ? "function-call" : "function-result",
        message: { ...message, contents: [content] },
        groupKey,
        toolName: typeof toolName === "string" ? toolName : "",
      });
    }

    flushNormalContents();
  }

  const toolGroups = new Map<string, ToolGroup>();
  for (const fragment of fragments) {
    if (!fragment.groupKey) {
      continue;
    }

    const group = toolGroups.get(fragment.groupKey) ?? { calls: [], results: [] };
    if (fragment.type === "function-call") {
      group.calls.push(fragment);
    } else if (fragment.type === "function-result") {
      group.results.push(fragment);
    }
    toolGroups.set(fragment.groupKey, group);
  }

  const renderedGroups = new Set<string>();
  for (const fragment of fragments) {
    if (fragment.type === "result") {
      items.push({ type: "result", message: fragment.message });
      continue;
    }

    if (fragment.type === "normal") {
      items.push({ type: "normal", message: fragment.message });
      continue;
    }

    const group = fragment.groupKey ? toolGroups.get(fragment.groupKey) : undefined;
    if (fragment.type === "function-result") {
      if (group && group.calls.length > 0) {
        continue;
      }

      items.push({ type: "normal", message: fragment.message });
      continue;
    }

    if (!fragment.groupKey || !group || group.results.length === 0) {
      items.push({ type: "normal", message: fragment.message });
      continue;
    }

    if (renderedGroups.has(fragment.groupKey)) {
      continue;
    }

    renderedGroups.add(fragment.groupKey);
    items.push({
      type: "accordion",
      messages: [...group.calls, ...group.results].map((item) => item.message),
      toolName: group.calls[0].toolName,
    });
  }

  return items;
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
