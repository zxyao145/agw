import assert from "node:assert/strict";
import test from "node:test";

import { createStreamingMessageBatcher, STREAMING_MESSAGE_BATCH_INTERVAL_MS } from "./batcher";

test("the batcher commits a burst once and drops an old generation", () => {
  const scheduled: Array<() => void> = [];
  const flushed: Array<{ messages: string[]; generation: number }> = [];
  const batcher = createStreamingMessageBatcher<string>(
    (messages, generation) => flushed.push({ messages, generation }),
    (callback, delay) => {
      assert.equal(delay, STREAMING_MESSAGE_BATCH_INTERVAL_MS);
      scheduled.push(callback);
      return scheduled.length;
    },
    () => undefined,
  );

  for (let index = 0; index < 100; index += 1) batcher.enqueue(String(index), 1);
  assert.equal(scheduled.length, 1);
  scheduled[0]();
  assert.equal(flushed.length, 1);
  assert.equal(flushed[0].messages.length, 100);

  batcher.enqueue("stale", 1);
  batcher.enqueue("current", 2);
  batcher.flush(2);

  assert.deepEqual(flushed[1], { messages: ["current"], generation: 2 });
});

test("discard cancels a scheduled flush", () => {
  const scheduled: Array<() => void> = [];
  let cancelCount = 0;
  const flushed: string[][] = [];
  const batcher = createStreamingMessageBatcher<string>(
    (messages) => flushed.push(messages),
    (callback) => {
      scheduled.push(callback);
      return scheduled.length;
    },
    () => {
      cancelCount += 1;
    },
  );

  batcher.enqueue("stale", 1);
  batcher.discard();
  scheduled[0]();

  assert.equal(cancelCount, 1);
  assert.deepEqual(flushed, []);
});
