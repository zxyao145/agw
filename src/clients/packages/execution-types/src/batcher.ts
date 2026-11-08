export interface StreamingMessageBatcher<T> {
  enqueue(message: T, generation: number): void;
  flush(generation: number): void;
  discard(): void;
}
