import type { ExecutionMessage, ExecutionMessageContent } from "./types";

const TEXT_CONTENT_TYPES = new Set(["TextContent", "text"]);
const FUNCTION_RESULT_CONTENT_TYPE = "FunctionResultContent";

function isReasoningContent(
  content: ExecutionMessageContent,
): content is ExecutionMessageContent & { content: string } {
  return content.type === "TextReasoningContent" && typeof content.content === "string";
}

export function isTextContent(
  content: ExecutionMessageContent,
): content is ExecutionMessageContent & { content: string } {
  return TEXT_CONTENT_TYPES.has(content.type) && typeof content.content === "string";
}

export function cloneMessageContent<T extends ExecutionMessageContent>(content: T): T {
  return {
    ...content,
    additionalProperties: content.additionalProperties
      ? { ...content.additionalProperties }
      : undefined,
  };
}

export function cloneMessage<T extends ExecutionMessage>(message: T): T {
  return {
    ...message,
    contents: message.contents.map(cloneMessageContent),
    additionalProperties: message.additionalProperties
      ? { ...message.additionalProperties }
      : undefined,
  };
}

function normalizeStreamingScopeId(message: ExecutionMessage): string | null {
  return message.streamingScopeId ?? null;
}

function hasSameStreamingIdentity(message: ExecutionMessage, incoming: ExecutionMessage): boolean {
  return (
    normalizeStreamingScopeId(message) === normalizeStreamingScopeId(incoming) &&
    message.messageId === incoming.messageId &&
    (message.role ?? null) === (incoming.role ?? null) &&
    (message.author ?? null) === (incoming.author ?? null)
  );
}

export function getStreamingIdentity(message: ExecutionMessage): string {
  return JSON.stringify([
    normalizeStreamingScopeId(message),
    message.messageId,
    message.role ?? null,
    message.author ?? null,
  ]);
}

function readString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim().length > 0 ? value : undefined;
}

export function getMessageStreamingScopeId(message: ExecutionMessage): string | undefined {
  return (
    readString(message.additionalProperties?.streamingScopeId) ??
    readString(message.streamingScopeId)
  );
}

function getFirstTextContent(
  contents: ExecutionMessageContent[],
): (ExecutionMessageContent & { content: string }) | undefined {
  return contents.find(isTextContent);
}

export function getMessageTextContent(message: ExecutionMessage): string {
  return getFirstTextContent(message.contents)?.content ?? "";
}

export function isUserTurnMessage(message: ExecutionMessage): boolean {
  return (
    message.role === "user" &&
    message.additionalProperties?.modelHistoryExcluded !== true &&
    !message.contents.some((content) => content.type === FUNCTION_RESULT_CONTENT_TYPE)
  );
}

export function appendStreamingContents(
  existing: ExecutionMessage,
  incoming: ExecutionMessage,
): void {
  for (const incomingContent of incoming.contents) {
    const previousContent = existing.contents.at(-1);
    const canAppendText =
      previousContent &&
      ((isTextContent(previousContent) && isTextContent(incomingContent)) ||
        (isReasoningContent(previousContent) && isReasoningContent(incomingContent)));
    if (canAppendText) {
      previousContent.content += incomingContent.content;
      continue;
    }

    existing.contents.push(cloneMessageContent(incomingContent));
  }
}

function cloneStreamingMessage<T extends ExecutionMessage>(message: T): T {
  const cloned = cloneMessage(message);
  cloned.contents = [];
  appendStreamingContents(cloned, message);
  return cloned;
}

export function scopeStreamingMessage<T extends ExecutionMessage>(
  message: T,
  streamingScopeId: string,
): T {
  return {
    ...cloneStreamingMessage(message),
    streamingScopeId,
  };
}

export function scopeMessagesByUserTurn<T extends ExecutionMessage>(messages: T[]): T[] {
  let currentScopeId: string | null = null;

  return messages.map((message, index) => {
    if (isUserTurnMessage(message)) {
      currentScopeId =
        getMessageStreamingScopeId(message) || message.messageId || `history-user-${index}`;
    }

    return scopeStreamingMessage(
      message,
      getMessageStreamingScopeId(message) ?? currentScopeId ?? `history-prelude-${index}`,
    );
  });
}

export function mergeStreamingMessage<T extends ExecutionMessage>(messages: T[], incoming: T): T[] {
  return mergeStreamingMessages(messages, [incoming]);
}

export function mergeStreamingMessages<T extends ExecutionMessage>(
  messages: T[],
  incomingMessages: T[],
): T[] {
  if (incomingMessages.length === 0) {
    return messages;
  }

  const updated = [...messages];
  let indexByIdentity: Map<string, number> | undefined;
  const mutableIndexes = new Set<number>();

  for (const incoming of incomingMessages) {
    const tailIndex = updated.length - 1;
    const existingIndex =
      tailIndex >= 0 && hasSameStreamingIdentity(updated[tailIndex], incoming)
        ? tailIndex
        : (() => {
            indexByIdentity ??= new Map(
              updated.map((message, index) => [getStreamingIdentity(message), index]),
            );
            return indexByIdentity.get(getStreamingIdentity(incoming));
          })();
    if (existingIndex === undefined) {
      const appendedIndex = updated.length;
      updated.push(cloneStreamingMessage(incoming));
      indexByIdentity?.set(getStreamingIdentity(incoming), appendedIndex);
      mutableIndexes.add(appendedIndex);
      continue;
    }

    if (!hasSameStreamingIdentity(updated[existingIndex], incoming)) {
      continue;
    }

    if (!mutableIndexes.has(existingIndex)) {
      updated[existingIndex] = cloneStreamingMessage(updated[existingIndex]);
      mutableIndexes.add(existingIndex);
    }
    appendStreamingContents(updated[existingIndex], incoming);
  }

  return updated;
}

export function mergeStreamingMessagesById<T extends ExecutionMessage>(messages: T[]): T[] {
  return mergeStreamingMessages([], messages);
}
