import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import { replaceExecutionHub, type TestHubConnection } from "@agw/test-harness";
import {
  ExecutionSession,
  getDurableExecutionStorageKey,
  type ExecutionAttachmentStore,
  type ExecutionRuntimeConfig,
  type ExecutionTrafficObserver,
} from "./execution-session";

const setting = { projectId: "project-1", conversationId: "conversation-1" };

function createStore(): ExecutionAttachmentStore & { values: Map<string, string> } {
  const values = new Map<string, string>();
  return {
    values,
    getItem: (key) => values.get(key) ?? null,
    setItem: (key, value) => void values.set(key, value),
    removeItem: (key) => void values.delete(key),
  };
}

function createRuntime(attachmentStore: ExecutionAttachmentStore | null): ExecutionRuntimeConfig {
  return { baseUrl: "https://agw.test", token: null, attachmentStore };
}

function readAttachment(
  runtime: ExecutionRuntimeConfig,
): { turnId: string; cursor: string | null } | null {
  const value = runtime.attachmentStore?.getItem(getDurableExecutionStorageKey(runtime, setting));
  return value ? JSON.parse(value) : null;
}

function request(turnId: string) {
  return {
    conversationId: "conversation-1",
    agentId: "agent-1",
    agentType: 0,
    turnId,
    input: { messageId: `input-${turnId}`, author: "$agw", contents: [] },
  };
}

function turnMessage(turnId: string, turnSequence: number): AiMessage {
  return {
    messageId: `${turnId}-${turnSequence}`,
    role: "assistant",
    author: "agent",
    contents: [{ type: "TextContent", content: `part ${turnSequence}` }],
    additionalProperties: { turnId, turnSequence },
  };
}

function turnFinished(turnId: string, turnSequence: number): AiMessage {
  return {
    messageId: `${turnId}-finished`,
    role: "system",
    author: "$agw",
    contents: [],
    additionalProperties: { type: "agw-turn-finished", status: "completed", turnId, turnSequence },
  };
}

function sequenceOf(message: AiMessage): number {
  return message.additionalProperties?.turnSequence as number;
}

/** 等待异步的重连恢复推进到断言成立。Waits for the asynchronous reconnect restore to make the assertion hold. */
async function waitUntil(condition: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 200 && !condition(); attempt++) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  assert.ok(condition(), "the condition did not hold in time");
}

/** 只记录消息的观察者；failing 中的序号在报告时抛出。An observer that records messages; sequences in failing throw when reported. */
function createObserver(failing = new Set<number>()) {
  const delivered: number[] = [];
  const observer: ExecutionTrafficObserver = {
    request: () => () => undefined,
    message(message) {
      if (failing.has(sequenceOf(message))) throw new Error("subscriber failed");
      delivered.push(sequenceOf(message));
    },
  };
  return { observer, delivered, failing };
}

test("a session reports every hub call before sending it and its outcome after it returns", async (t) => {
  const failure = new Error("checkpoints unavailable");
  const hubs = replaceExecutionHub(t, {
    provider: "InProcess",
    onInvoke: ({ method }) => {
      if (method === "GetAgentflowCheckpoints") throw failure;
    },
  });
  const events: unknown[] = [];
  const observer: ExecutionTrafficObserver = {
    request(method, args) {
      events.push({ request: method, args, sent: hubs[0]!.invocations.length });
      return (outcome) => events.push({ response: method, outcome });
    },
    message() {},
  };
  const session = new ExecutionSession({ onMessage() {} }, createRuntime(null), observer);
  try {
    await session.configure(setting);
    await assert.rejects(session.listAgentflowCheckpoints("flow-1"), (error) => error === failure);

    assert.deepEqual(events, [
      { request: "GetExecutionProvider", args: [], sent: 0 },
      { response: "GetExecutionProvider", outcome: { ok: true, value: "InProcess" } },
      { request: "DispatchCommand", args: [hubs[0]!.invocations[1]!.args[0]], sent: 1 },
      { response: "DispatchCommand", outcome: { ok: true, value: undefined } },
      { request: "FindInProcessExecution", args: ["project-1", "conversation-1"], sent: 2 },
      { response: "FindInProcessExecution", outcome: { ok: true, value: null } },
      { request: "GetAgentflowCheckpoints", args: ["flow-1"], sent: 3 },
      { response: "GetAgentflowCheckpoints", outcome: { ok: false, error: failure } },
    ]);
    assert.equal((hubs[0]!.invocations[1]!.args[0] as { type: string }).type, "SettingCommand");
  } finally {
    await session.dispose();
  }
});

test("a throwing request observer keeps the hub call from being sent", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  const rejection = new Error("observer failed");
  let rejectDispatch = false;
  const observer: ExecutionTrafficObserver = {
    request(method) {
      if (rejectDispatch && method === "DispatchCommand") throw rejection;
      return () => undefined;
    },
    message() {},
  };
  const session = new ExecutionSession({ onMessage() {} }, createRuntime(null), observer);
  try {
    await session.configure(setting);
    const sent = hubs[0]!.invocations.length;
    rejectDispatch = true;

    await assert.rejects(session.setMode("agent-1", "plan"), (error) => error === rejection);

    assert.equal(hubs[0]!.invocations.length, sent);
  } finally {
    await session.dispose();
  }
});

test("pushed messages reach the handlers first and the observer gets the server's message", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "InProcess" });
  const order: string[] = [];
  const observed: AiMessage[] = [];
  const session = new ExecutionSession(
    { onMessage: (message) => order.push(`handler:${message.streamingScopeId}`) },
    createRuntime(null),
    {
      request: () => () => undefined,
      message(message) {
        order.push("observer");
        observed.push(message);
      },
    },
  );
  const message: AiMessage = {
    messageId: "text-1",
    role: "assistant",
    author: "agent",
    contents: [{ type: "TextContent", content: "hello" }],
  };
  await session.configure(setting);

  hubs[0]!.receive(message);
  await session.dispose();
  hubs[0]!.receive({ ...message, messageId: "after-dispose" });

  assert.deepEqual(order, ["handler:text-1", "observer"]);
  assert.equal(observed[0], message);
  assert.equal("streamingScopeId" in observed[0]!, false);
});

test("a durable cursor commits only after the handler and the observer both return", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "Distributed" });
  const runtime = createRuntime(createStore());
  const seen: (string | null | undefined)[] = [];
  const session = new ExecutionSession(
    { onMessage: () => seen.push(readAttachment(runtime)?.cursor) },
    runtime,
    { request: () => () => undefined, message: () => seen.push(readAttachment(runtime)?.cursor) },
  );
  try {
    await session.configure(setting);
    await session.execute(request("turn-1"));

    hubs[0]!.receive(turnMessage("turn-1", 1));

    assert.deepEqual(seen, [null, null]);
    assert.deepEqual(readAttachment(runtime), { turnId: "turn-1", cursor: "1" });

    // 没有观察者时，处理函数返回就确认这条消息。Without an observer, the handler returning confirms the message.
    const otherRuntime = createRuntime(createStore());
    const handlerOnly = new ExecutionSession({ onMessage() {} }, otherRuntime);
    await handlerOnly.configure(setting);
    await handlerOnly.execute(request("turn-2"));
    hubs.at(-1)!.receive(turnMessage("turn-2", 1));
    assert.deepEqual(readAttachment(otherRuntime), { turnId: "turn-2", cursor: "1" });
    await handlerOnly.dispose();
  } finally {
    await session.dispose();
  }
});

test("a failed position holds its cursor and later positions until the replay succeeds", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "Distributed" });
  const runtime = createRuntime(createStore());
  const { observer, delivered, failing } = createObserver(new Set([3]));
  const handled: number[] = [];
  const session = new ExecutionSession(
    { onMessage: (message) => handled.push(sequenceOf(message)) },
    runtime,
    observer,
  );
  try {
    await session.configure(setting);
    await session.execute(request("turn-1"));
    const hub = hubs[0]!;
    hub.receive(turnMessage("turn-1", 1));
    hub.receive(turnMessage("turn-1", 2));

    assert.throws(() => hub.receive(turnMessage("turn-1", 3)), /subscriber failed/);
    hub.receive(turnMessage("turn-1", 4));

    assert.deepEqual(handled, [1, 2, 3]);
    assert.deepEqual(delivered, [1, 2]);
    assert.deepEqual(readAttachment(runtime), { turnId: "turn-1", cursor: "2" });
    assert.equal(session.hasRunningTurn(), true);

    // 自动重连从已确认的位置恢复订阅。The automatic reconnect resubscribes from the confirmed position.
    hub.reconnect();
    await waitUntil(() => hub.commands("SubscribeTurnCommand").length === 1);
    assert.equal(hub.commands("SubscribeTurnCommand")[0]!.cursor, "2");

    // 恢复期间再次失败，位置仍保持不变。Failing again during the replay keeps the position.
    assert.throws(() => hub.receive(turnMessage("turn-1", 3)), /subscriber failed/);
    assert.deepEqual(readAttachment(runtime), { turnId: "turn-1", cursor: "2" });

    failing.clear();
    hub.receive(turnMessage("turn-1", 3));
    hub.receive(turnMessage("turn-1", 4));

    assert.deepEqual(delivered, [1, 2, 3, 4]);
    assert.deepEqual(readAttachment(runtime), { turnId: "turn-1", cursor: "4" });
  } finally {
    await session.dispose();
  }
});

test("a manual reconnect and a reloaded session resubscribe from the confirmed position", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "Distributed" });
  const runtime = createRuntime(createStore());
  const { observer } = createObserver(new Set([2]));
  const session = new ExecutionSession({ onMessage() {} }, runtime, observer);
  const reloaded = new ExecutionSession({ onMessage() {} }, runtime);
  try {
    await session.configure(setting);
    await session.execute(request("turn-1"));
    const hub = hubs[0]!;
    hub.receive(turnMessage("turn-1", 1));
    assert.throws(() => hub.receive(turnMessage("turn-1", 2)), /subscriber failed/);

    hub.close(new Error("connection lost"));
    await session.retryConnection();
    await reloaded.configure(setting);

    assert.equal(hub.commands("SubscribeTurnCommand")[0]!.cursor, "1");
    assert.equal(hubs[1]!.commands("SubscribeTurnCommand")[0]!.cursor, "1");
  } finally {
    await session.dispose();
    await reloaded.dispose();
  }
});

test("a turn whose first message fails resubscribes without a cursor", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "Distributed" });
  const runtime = createRuntime(createStore());
  const { observer } = createObserver(new Set([1]));
  const session = new ExecutionSession({ onMessage() {} }, runtime, observer);
  try {
    await session.configure(setting);
    await session.execute(request("turn-1"));
    const hub = hubs[0]!;

    assert.throws(() => hub.receive(turnMessage("turn-1", 1)), /subscriber failed/);
    hub.reconnect();
    await waitUntil(() => hub.commands("SubscribeTurnCommand").length === 1);

    assert.equal(hub.commands("SubscribeTurnCommand")[0]!.cursor ?? null, null);
    assert.deepEqual(readAttachment(runtime), { turnId: "turn-1", cursor: null });
  } finally {
    await session.dispose();
  }
});

test("a handler failure keeps the message from the observer and the cursor in place", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "Distributed" });
  const runtime = createRuntime(createStore());
  const { observer, delivered } = createObserver();
  let failHandler = true;
  const session = new ExecutionSession(
    {
      onMessage(message) {
        if (failHandler && sequenceOf(message) === 2) throw new Error("handler failed");
      },
    },
    runtime,
    observer,
  );
  try {
    await session.configure(setting);
    await session.execute(request("turn-1"));
    const hub = hubs[0]!;
    hub.receive(turnMessage("turn-1", 1));

    assert.throws(() => hub.receive(turnMessage("turn-1", 2)), /handler failed/);

    assert.deepEqual(delivered, [1]);
    assert.deepEqual(readAttachment(runtime), { turnId: "turn-1", cursor: "1" });
    hub.reconnect();
    await waitUntil(() => hub.commands("SubscribeTurnCommand").length === 1);
    assert.equal(hub.commands("SubscribeTurnCommand")[0]!.cursor, "1");
    failHandler = false;
    hub.receive(turnMessage("turn-1", 2));
    assert.deepEqual(delivered, [1, 2]);
    assert.deepEqual(readAttachment(runtime), { turnId: "turn-1", cursor: "2" });
  } finally {
    await session.dispose();
  }
});

test("a failed finish keeps the attachment, the running turn and its waiters until the replay succeeds", async (t) => {
  const hubs = replaceExecutionHub(t, { provider: "Distributed" });
  const runtime = createRuntime(createStore());
  const { observer, failing } = createObserver(new Set([3]));
  const session = new ExecutionSession({ onMessage() {} }, runtime, observer);
  try {
    await session.configure(setting);
    await session.execute(request("turn-1"));
    const hub = hubs[0]!;
    hub.receive(turnMessage("turn-1", 1));
    hub.receive(turnMessage("turn-1", 2));

    assert.throws(() => hub.receive(turnFinished("turn-1", 3)), /subscriber failed/);
    let stopped = false;
    const stopping = session.interruptAndWait().then(() => {
      stopped = true;
    });
    await waitUntil(() => hub.commands("InterruptCommand").length === 1);
    // 不属于 Turn 的结束通知不能清除待恢复的 Turn。A finish notice outside any turn cannot clear the turn awaiting recovery.
    hub.receive({
      ...turnFinished("turn-1", 3),
      additionalProperties: { type: "agw-turn-finished" },
    });

    assert.deepEqual(readAttachment(runtime), { turnId: "turn-1", cursor: "2" });
    assert.equal(session.hasRunningTurn(), true);
    assert.equal(stopped, false);
    await assert.rejects(session.execute(request("turn-2")), /already has a running task/);

    hub.reconnect();
    await waitUntil(() => hub.commands("SubscribeTurnCommand").length === 1);
    assert.equal(hub.commands("SubscribeTurnCommand")[0]!.cursor, "2");
    assert.throws(() => hub.receive(turnFinished("turn-1", 3)), /subscriber failed/);
    assert.deepEqual(readAttachment(runtime), { turnId: "turn-1", cursor: "2" });

    failing.clear();
    hub.receive(turnFinished("turn-1", 3));
    await stopping;

    assert.equal(readAttachment(runtime), null);
    assert.equal(session.hasRunningTurn(), false);
    assert.equal(stopped, true);
  } finally {
    await session.dispose();
  }
});

test("a failed finish is cleared when the server confirms the execution no longer exists", async (t) => {
  const hubs = replaceExecutionHub(t, {
    provider: "Distributed",
    onInvoke: ({ args }) => {
      if ((args[0] as { type?: string } | undefined)?.type === "SubscribeTurnCommand") {
        throw new Error("HubException: 4040011: Durable execution was not found.");
      }
    },
  });
  const runtime = createRuntime(createStore());
  const { observer } = createObserver(new Set([2]));
  const session = new ExecutionSession({ onMessage() {} }, runtime, observer);
  try {
    await session.configure(setting);
    await session.execute(request("turn-1"));
    const hub: TestHubConnection = hubs[0]!;
    hub.receive(turnMessage("turn-1", 1));
    assert.throws(() => hub.receive(turnFinished("turn-1", 2)), /subscriber failed/);

    hub.reconnect();
    await waitUntil(() => !session.hasRunningTurn());

    assert.equal(readAttachment(runtime), null);
  } finally {
    await session.dispose();
  }
});
