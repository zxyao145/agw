export const STREAMING_MESSAGE_BATCH_INTERVAL_MS = 50;

type StreamingMessageBatchTimer = ReturnType<typeof setTimeout>;
type StreamingMessageBatchSchedule = (
  callback: () => void,
  delay: number,
) => StreamingMessageBatchTimer;

export interface StreamingMessageBatcher<T> {
  enqueue(message: T, generation: number): void;
  flush(generation: number): void;
  discard(): void;
}

/**
 * 将同一执行 generation 内的高频增量合并成固定间隔的提交。
 * generation 变化时会丢弃尚未提交的旧执行消息。
 */
export function createStreamingMessageBatcher<T>(
  onFlush: (messages: T[], generation: number) => void,
  schedule: StreamingMessageBatchSchedule = setTimeout,
  cancel: (timer: StreamingMessageBatchTimer) => void = clearTimeout,
): StreamingMessageBatcher<T> {
  let timer: StreamingMessageBatchTimer | undefined;
  let generation: number | undefined;
  let bufferedMessages: T[] = [];

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
