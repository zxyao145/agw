import assert from "node:assert/strict";
import test from "node:test";
import { createBearerApiClient, type AiMessage } from "@agw/api";
import { ChatHttpClient } from "@agw/chat-runtime/chat-http-client";
import { ExecutionSession, type ExecutionHubHandlers } from "@agw/chat-runtime/execution-session";
import type { ChatEvent } from "@agw/execution-core";
import { replaceExecutionHub } from "@agw/test-harness";
import { NativeExecutionSession } from "./native-execution-session";

type ExecutionOutcome = "pending" | "completed" | Error;

// Reads the outcome after asynchronous settlement; a direct property read would keep the
// narrowing from an earlier assertion.
// 在异步落定后读取结果；直接读取属性会沿用此前断言留下的类型收窄。
function currentOutcome(result: { outcome: ExecutionOutcome }): ExecutionOutcome {
  return result.outcome;
}

const request = {
  conversationId: "conversation",
  agentId: "agent",
  agentType: 0 as const,
  turnId: "turn",
  input: { messageId: "message", author: "$agw", contents: [] },
};

const scope = { serverId: "server-1", projectId: "project-1", conversationId: "conversation-1" };

function createChatClient(): ChatHttpClient {
  return new ChatHttpClient({
    api: createBearerApiClient({ baseUrl: "https://agw.test", token: "token" }),
    identity: () => ({ baseUrl: "https://agw.test", token: "token" }),
    execution: () => ({
      baseUrl: "https://agw.test",
      token: "token",
      withCredentials: false,
      webSocket: globalThis.WebSocket,
      attachmentStore: null,
    }),
  });
}

test("messages the native session receives are dispatched as message events of its conversation scope", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  const client = createChatClient();
  const events: ChatEvent<AiMessage>[] = [];
  client.subscribe({ kinds: ["message"] }, (event) => events.push(event));
  const received: string[] = [];
  const native = new NativeExecutionSession({
    client,
    scope,
    onMessage: (message) => received.push(String(message.messageId)),
  });
  try {
    await native.configure({
      projectId: "project-1",
      conversationId: "conversation-1",
      permissionMode: "fullAccess",
    });
    hubs[0]!.receive({
      messageId: "text-1",
      role: "assistant",
      author: "agent",
      contents: [{ type: "TextContent", content: "Hello" }],
    });

    assert.deepEqual(received, ["text-1"]);
    assert.deepEqual(
      events.map((event) => ({ kind: event.kind, scope: event.scope })),
      [{ kind: "message", scope }],
    );
  } finally {
    await native.dispose();
  }
});

test("native execution settles with an unknown-outcome error when reconnect confirms idle without a terminal message", async (t) => {
  let active = false;
  t.mock.method(ExecutionSession.prototype, "execute", async () => {
    active = true;
  });
  t.mock.method(ExecutionSession.prototype, "hasRunningTurn", () => active);
  t.mock.method(ExecutionSession.prototype, "dispose", async () => {});
  const reconnectStates: unknown[] = [];
  const native = new NativeExecutionSession({
    client: createChatClient(),
    scope,
    onMessage() {},
    onReconnecting: (state) => reconnectStates.push(state),
  });
  const handlers = (native as unknown as { session: { handlers: ExecutionHubHandlers } }).session
    .handlers;
  const result: { outcome: ExecutionOutcome } = { outcome: "pending" };
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
    const outcome = currentOutcome(result);
    assert.ok(
      outcome instanceof Error,
      "idle recovery must release the waiter without claiming success",
    );
    assert.match(outcome.message, /outcome.*unknown/i);
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
    client: createChatClient(),
    scope,
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
  t.mock.method(ExecutionSession.prototype, "hasRunningTurn", () => active);
  t.mock.method(ExecutionSession.prototype, "dispose", async () => {});
  const native = new NativeExecutionSession({
    client: createChatClient(),
    scope,
    onMessage() {},
  });
  const handlers = (native as unknown as { session: { handlers: ExecutionHubHandlers } }).session
    .handlers;
  const result: { outcome: ExecutionOutcome } = { outcome: "pending" };
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
    const outcome = currentOutcome(result);
    assert.ok(outcome instanceof Error);
    assert.match(outcome.message, /outcome.*unknown/i);
  } finally {
    await native.dispose();
    await execution;
  }
});

test("native rejects a pre-existing active execution instead of adopting it as an unacknowledged start", async (t) => {
  t.mock.method(ExecutionSession.prototype, "hasRunningTurn", () => true);
  const execute = t.mock.method(ExecutionSession.prototype, "execute", async () => {
    throw new Error("This conversation already has a running task.");
  });
  t.mock.method(ExecutionSession.prototype, "dispose", async () => {});
  const native = new NativeExecutionSession({
    client: createChatClient(),
    scope,
    onMessage() {},
  });
  const result: { outcome: ExecutionOutcome } = { outcome: "pending" };
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
