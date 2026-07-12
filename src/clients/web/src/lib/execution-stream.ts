import { Ulid } from "id128";
import { MessageContentType, type AiMessage, type AiMessageContent } from "@/types";

export type ExecutionUserInput = Pick<AiMessage, "messageId" | "author" | "contents">;

const default_user = "$agw";
const TEXT_CONTENT_TYPES = new Set(["TextContent", "text"]);

function isTextContent(content: AiMessageContent): content is AiMessageContent & { content: string } {
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

export function createUserTextMessage(input: string): AiMessage {
  return {
    messageId: Ulid.generate().toCanonical(),
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
  const existingIndex = messages.findIndex((message) => message.messageId === incoming.messageId);
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
