import assert from "node:assert/strict";
import test from "node:test";
import { ExecutionSession, type ExecutionHubHandlers } from "@agw/chat-runtime/execution-session";
import { NativeExecutionSession } from "./native-execution-session";

const request = {
  conversationId: "conversation",
  agentId: "agent",
  agentType: 0 as const,
  executionId: "execution",
  input: { messageId: "message", author: "$agw", contents: [] },
};

test("native execution settles with an unknown-outcome error when reconnect confirms idle without a terminal message", async (t) => {
  let active = false;
  t.mock.method(ExecutionSession.prototype, "execute", async () => {
    active = true;
  });
  t.mock.method(ExecutionSession.prototype, "hasActiveExecution", () => active);
  t.mock.method(ExecutionSession.prototype, "dispose", async () => {});
  const reconnectStates: unknown[] = [];
  const native = new NativeExecutionSession({
    serverUrl: "https://agw.test",
    token: "token",
    onMessage() {},
    onReconnecting: (state) => reconnectStates.push(state),
  });
  const handlers = (native as unknown as { session: { handlers: ExecutionHubHandlers } }).session
    .handlers;
  const result: { outcome: "pending" | "completed" | Error } = { outcome: "pending" };
  const execution = native.execute(request).then(
    () => {
      result.outcome = "completed";
    },
    (error: Error) => {
      result.outcome = error;
    },
  );
  try {
    handlers.onReconnected?.();
    for (let i = 0; i < 10; i++) await Promise.resolve();
    assert.equal(result.outcome, "pending", "an active recovered execution must keep waiting");
    active = false;
    handlers.onReconnected?.();
    for (let i = 0; i < 10; i++) await Promise.resolve();
    assert.ok(
      result.outcome instanceof Error,
      "idle recovery must release the waiter without claiming success",
    );
    assert.match(result.outcome.message, /outcome.*unknown/i);
    assert.equal(reconnectStates.at(-1), null);
  } finally {
    await native.dispose();
    await execution;
  }
});

test("native startup rejection releases its terminal waiter without an unhandled rejection", async (t) => {
  t.mock.method(ExecutionSession.prototype, "execute", async () => {
    throw new Error("Start rejected");
  });
  t.mock.method(ExecutionSession.prototype, "dispose", async () => {});
  const native = new NativeExecutionSession({
    serverUrl: "https://agw.test",
    token: "token",
    onMessage() {},
  });
  try {
    await assert.rejects(native.execute(request), /Start rejected/);
    await new Promise<void>((resolve) => setImmediate(resolve));
  } finally {
    await native.dispose();
  }
});

test("native execution keeps waiting after an unacknowledged start while the core still tracks an active turn", async (t) => {
  let active = false;
  t.mock.method(ExecutionSession.prototype, "execute", async () => {
    active = true;
    throw new Error("Execution acknowledgement lost");
  });
  t.mock.method(ExecutionSession.prototype, "hasActiveExecution", () => active);
  t.mock.method(ExecutionSession.prototype, "dispose", async () => {});
  const native = new NativeExecutionSession({
    serverUrl: "https://agw.test",
    token: "token",
    onMessage() {},
  });
  const handlers = (native as unknown as { session: { handlers: ExecutionHubHandlers } }).session
    .handlers;
  const result: { outcome: "pending" | "completed" | Error } = { outcome: "pending" };
  const execution = native.execute(request).then(
    () => {
      result.outcome = "completed";
    },
    (error: Error) => {
      result.outcome = error;
    },
  );
  try {
    await new Promise<void>((resolve) => setImmediate(resolve));
    assert.equal(
      result.outcome,
      "pending",
      "an uncertain ACK must not release the UI's active execution wait",
    );
    handlers.onReconnected?.();
    for (let i = 0; i < 10; i++) await Promise.resolve();
    assert.equal(result.outcome, "pending", "recovery confirming active must keep the same waiter");
    active = false;
    handlers.onReconnected?.();
    for (let i = 0; i < 10; i++) await Promise.resolve();
    assert.ok(result.outcome instanceof Error);
    assert.match(result.outcome.message, /outcome.*unknown/i);
  } finally {
    await native.dispose();
    await execution;
  }
});

test("native rejects a pre-existing active execution instead of adopting it as an unacknowledged start", async (t) => {
  t.mock.method(ExecutionSession.prototype, "hasActiveExecution", () => true);
  const execute = t.mock.method(ExecutionSession.prototype, "execute", async () => {
    throw new Error("This conversation already has a running task.");
  });
  t.mock.method(ExecutionSession.prototype, "dispose", async () => {});
  const native = new NativeExecutionSession({
    serverUrl: "https://agw.test",
    token: "token",
    onMessage() {},
  });
  const result: { outcome: "pending" | "completed" | Error } = { outcome: "pending" };
  const execution = native.execute(request).then(
    () => {
      result.outcome = "completed";
    },
    (error: Error) => {
      result.outcome = error;
    },
  );
  try {
    for (let i = 0; i < 10; i++) await Promise.resolve();
    assert.ok(
      result.outcome instanceof Error,
      "a different active execution must fail immediately",
    );
    assert.match(result.outcome.message, /already has a running task/);
    assert.equal(execute.mock.callCount(), 0);
  } finally {
    await native.dispose();
    await execution;
  }
});
