import { createUuidV7 } from "@agw/api";
import { MessageContentType, type AiMessage } from "@agw/api";
import {
  createStreamingMessageBatcher as createCoreStreamingMessageBatcher,
  type StreamingMessageBatcher as CoreStreamingMessageBatcher,
} from "@agw/execution-core";
import type { ChatImageAttachment } from "../lib/chat/image-attachments";
import type { ExecutionUserInput } from "./execution-hub";

export {
  getMessageTextContent,
  mergeStreamingMessage,
  mergeStreamingMessages,
  mergeStreamingMessagesById,
  scopeMessagesByUserTurn,
  scopeStreamingMessage,
  STREAMING_MESSAGE_BATCH_INTERVAL_MS,
} from "@agw/execution-core";

const default_user = "$agw";

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
    contents: message.contents.map((content) => ({
      ...content,
      additionalProperties: content.additionalProperties
        ? { ...content.additionalProperties }
        : undefined,
    })),
  };
}

type StreamingMessageBatchTimer = ReturnType<typeof setTimeout>;
type StreamingMessageBatchSchedule = (
  callback: () => void,
  delay: number,
) => StreamingMessageBatchTimer;

export type StreamingMessageBatcher = CoreStreamingMessageBatcher<AiMessage>;

export function createStreamingMessageBatcher(
  onFlush: (messages: AiMessage[], generation: number) => void,
  schedule: StreamingMessageBatchSchedule = setTimeout,
  cancel: (timer: StreamingMessageBatchTimer) => void = clearTimeout,
): StreamingMessageBatcher {
  return createCoreStreamingMessageBatcher(onFlush, schedule, cancel);
}
