import { createUuidV7 } from "@agw/api";
import { MessageContentType, type AiMessage, type AiMessageContent } from "@agw/api";
import type { ExecutionUserInput } from "./execution-hub";

const default_user = "$agw";
const TEXT_CONTENT_TYPES = new Set(["TextContent", "text"]);

function isTextContent(
  content: AiMessageContent,
): content is AiMessageContent & { content: string } {
  return TEXT_CONTENT_TYPES.has(content.type) && typeof content.content === "string";
}

function cloneMessageContent(content: AiMessageContent): AiMessageContent {
  return {
    ...content,
    additionalProperties: content.additionalProperties
      ? { ...content.additionalProperties }
      : undefined,
  };
}

function cloneMessage(message: AiMessage): AiMessage {
  return {
    ...message,
    contents: message.contents.map(cloneMessageContent),
    additionalProperties: message.additionalProperties
      ? { ...message.additionalProperties }
      : undefined,
  };
}

function hasSameStreamingIdentity(message: AiMessage, incoming: AiMessage): boolean {
  return (
    message.streamingScopeId === incoming.streamingScopeId &&
    message.messageId === incoming.messageId &&
    message.role === incoming.role &&
    message.author === incoming.author
  );
}

export function scopeStreamingMessage(message: AiMessage, streamingScopeId: string): AiMessage {
  return {
    ...cloneMessage(message),
    streamingScopeId,
  };
}

export function scopeMessagesByUserTurn(messages: AiMessage[]): AiMessage[] {
  let currentScopeId: string | null = null;

  return messages.map((message, index) => {
    if (message.role === "user") {
      currentScopeId = message.messageId || `history-user-${index}`;
    }

    return scopeStreamingMessage(message, currentScopeId ?? `history-prelude-${index}`);
  });
}

export function createUserTextMessage(input: string): AiMessage {
  return {
    messageId: createUuidV7(),
    author: default_user,
    role: "user",
    contents: [{ type: MessageContentType.TextContent, content: input }],
  };
}

export function toExecutionUserInput(message: AiMessage): ExecutionUserInput {
  return {
    messageId: message.messageId,
    author: message.author,
    contents: message.contents.map(cloneMessageContent),
  };
}

export function getMessageTextContent(message: AiMessage): string {
  return message.contents.find(isTextContent)?.content ?? "";
}

export function mergeStreamingMessage(messages: AiMessage[], incoming: AiMessage): AiMessage[] {
  const existingIndex = messages.findIndex((message) =>
    hasSameStreamingIdentity(message, incoming),
  );
  if (existingIndex < 0) {
    return [...messages, cloneMessage(incoming)];
  }

  const updated = [...messages];
  const existing = cloneMessage(updated[existingIndex]);
  const existingText = existing.contents.find(isTextContent);
  const incomingText = incoming.contents.find(isTextContent);

  if (incomingText) {
    if (existingText) {
      existingText.content = (existingText.content || "") + (incomingText.content || "");
    } else {
      existing.contents.push(cloneMessageContent(incomingText));
    }
  }

  const incomingNonTextContents = incoming.contents
    .filter((content) => !isTextContent(content))
    .map(cloneMessageContent);
  if (incomingNonTextContents.length > 0) {
    existing.contents = [...existing.contents, ...incomingNonTextContents];
  }

  updated[existingIndex] = existing;
  return updated;
}

export function mergeStreamingMessagesById(messages: AiMessage[]): AiMessage[] {
  return messages.reduce<AiMessage[]>(
    (accumulator, message) => mergeStreamingMessage(accumulator, message),
    [],
  );
}
