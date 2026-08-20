import { createUuidV7 } from "@agw/api";
import { MessageContentType, type AiMessage, type AiMessageContent } from "@agw/api";
import type { ChatImageAttachment } from "../lib/chat/image-attachments";
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

function getStreamingIdentity(message: AiMessage): string {
  return JSON.stringify([
    message.streamingScopeId ?? null,
    message.messageId,
    message.role,
    message.author ?? null,
  ]);
}

function appendStreamingContents(existing: AiMessage, incoming: AiMessage): void {
  for (const incomingContent of incoming.contents) {
    const previousContent = existing.contents.at(-1);
    if (previousContent && isTextContent(previousContent) && isTextContent(incomingContent)) {
      previousContent.content += incomingContent.content;
      continue;
    }

    existing.contents.push(cloneMessageContent(incomingContent));
  }
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
  return createUserMessage(input, []);
}

export function createUserMessage(
  input: string,
  imageAttachments: readonly ChatImageAttachment[],
): AiMessage {
  const contents: AiMessage["contents"] = imageAttachments.map((attachment) => ({
    type: MessageContentType.DataContent,
    uri: attachment.dataUrl,
    name: attachment.name,
  }));
  if (input.trim()) {
    contents.push({ type: MessageContentType.TextContent, content: input });
  }

  return {
    messageId: createUuidV7(),
    author: default_user,
    role: "user",
    contents,
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
  return mergeStreamingMessages(messages, [incoming]);
}

export function mergeStreamingMessages(
  messages: AiMessage[],
  incomingMessages: AiMessage[],
): AiMessage[] {
  if (incomingMessages.length === 0) {
    return messages;
  }

  const updated = [...messages];
  const indexByIdentity = new Map(
    messages.map((message, index) => [getStreamingIdentity(message), index]),
  );
  const mutableIndexes = new Set<number>();

  for (const incoming of incomingMessages) {
    const identity = getStreamingIdentity(incoming);
    const existingIndex = indexByIdentity.get(identity);
    if (existingIndex === undefined) {
      const appendedIndex = updated.length;
      updated.push(cloneMessage(incoming));
      indexByIdentity.set(identity, appendedIndex);
      mutableIndexes.add(appendedIndex);
      continue;
    }

    if (!hasSameStreamingIdentity(updated[existingIndex], incoming)) {
      continue;
    }

    if (!mutableIndexes.has(existingIndex)) {
      updated[existingIndex] = cloneMessage(updated[existingIndex]);
      mutableIndexes.add(existingIndex);
    }
    appendStreamingContents(updated[existingIndex], incoming);
  }

  return updated;
}

export function mergeStreamingMessagesById(messages: AiMessage[]): AiMessage[] {
  return mergeStreamingMessages([], messages);
}

export const STREAMING_MESSAGE_BATCH_INTERVAL_MS = 50;

type StreamingMessageBatchTimer = ReturnType<typeof setTimeout>;
type StreamingMessageBatchSchedule = (
  callback: () => void,
  delay: number,
) => StreamingMessageBatchTimer;

export interface StreamingMessageBatcher {
  enqueue(message: AiMessage, generation: number): void;
  flush(generation: number): void;
  discard(): void;
}

export function createStreamingMessageBatcher(
  onFlush: (messages: AiMessage[], generation: number) => void,
  schedule: StreamingMessageBatchSchedule = setTimeout,
  cancel: (timer: StreamingMessageBatchTimer) => void = clearTimeout,
): StreamingMessageBatcher {
  let timer: StreamingMessageBatchTimer | undefined;
  let generation: number | undefined;
  let bufferedMessages: AiMessage[] = [];

  const cancelTimer = () => {
    if (timer !== undefined) {
      cancel(timer);
      timer = undefined;
    }
  };

  const discard = () => {
    cancelTimer();
    generation = undefined;
    bufferedMessages = [];
  };

  const flush = (currentGeneration: number) => {
    cancelTimer();
    if (generation !== currentGeneration || bufferedMessages.length === 0) {
      discard();
      return;
    }

    const messages = bufferedMessages;
    bufferedMessages = [];
    generation = undefined;
    onFlush(messages, currentGeneration);
  };

  return {
    enqueue(message, currentGeneration) {
      if (generation !== undefined && generation !== currentGeneration) {
        discard();
      }

      generation = currentGeneration;
      bufferedMessages.push(message);
      if (timer === undefined) {
        timer = schedule(() => flush(currentGeneration), STREAMING_MESSAGE_BATCH_INTERVAL_MS);
      }
    },
    flush,
    discard,
  };
}
