import type { AiMessage } from "@agw/api";
import {
  createStreamingMessageBatcher as createCoreStreamingMessageBatcher,
  type StreamingMessageBatcher as CoreStreamingMessageBatcher,
} from "@agw/execution-core";
export { createUserMessage, createUserTextMessage, toExecutionUserInput } from "@agw/chat-core";

export {
  getMessageTextContent,
  mergeStreamingMessage,
  mergeStreamingMessages,
  mergeStreamingMessagesById,
  scopeMessagesByUserTurn,
  scopeStreamingMessage,
  STREAMING_MESSAGE_BATCH_INTERVAL_MS,
} from "@agw/execution-core";

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
