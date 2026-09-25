import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import type { ExecutionHubHandlers, ExecutionReconnectState } from "./execution-hub";
import { ExecutionSessionManager } from "./execution-session-manager";

const sessionKey = {
  serverId: "server-a",
  projectId: "project-1",
  contextId: "context-1",
};

/** 创建满足会话管理器测试所需的最小执行客户端。 */
function createExecutionClient() {
  return {
    configure: async () => ({ restoredDurableExecution: false }),
    hasActiveExecution: () => false,
    execute: async () => undefined,
    listAgentflowCheckpoints: async () => [],
    resumeCheckpoint: async () => "execution-resumed",
    setMode: async () => undefined,
    setPermissionMode: async () => undefined,
    interrupt: async () => undefined,
    interruptAndWait: async () => undefined,
    submitHumanResponse: async () => undefined,
    retryConnection: async () => undefined,
    dispose: async () => undefined,
  };
}

function createQuestionInteraction(interactionId = "interaction-1"): AiMessage {
  return {
    messageId: `message-${interactionId}`,
    role: "system",
    author: "$agw",
    contents: [{ type: "TextContent", content: "Choose before continuing." }],
    additionalProperties: {
      type: "interaction-request",
      interaction: {
        kind: "user-input",
        interactionId,
        inputKind: "questions",
        source: { toolName: "ask_user_question", callId: "call-1" },
        prompt: "Choose before continuing.",
        payload: {
          questions: [
            {
              question: "What should happen next?",
              header: "Next step",
              multiSelect: false,
              options: [
                { label: "Continue", description: "Keep running the workflow." },
                { label: "Stop", description: "Stop the workflow." },
              ],
            },
          ],
        },
      },
    },
  };
}

function createGateInteraction(interactionId: string): AiMessage {
  return {
    messageId: `message-${interactionId}`,
    role: "system",
    contents: [],
    additionalProperties: {
      type: "interaction-request",
      interaction: {
        kind: "workflow-gate",
        interactionId,
        prompt: "Continue?",
        mode: "approval",
        source: { nodeId: `node-${interactionId}` },
      },
    },
  };
}

for (const detachedDuringPublication of [false, true]) {
  test(`parallel gates survive detach and replay once (published detached: ${detachedDuringPublication})`, async () => {
    let handlers!: ExecutionHubHandlers;
    const manager = new ExecutionSessionManager((value) => {
      handlers = value;
      return createExecutionClient();
    });
    const handle = manager.attach(sessionKey, { onMessage: () => undefined });
    if (detachedDuringPublication) handle.detach();
    const first = createGateInteraction("first");
    const second = createGateInteraction("second");
    handlers.onMessage(first);
    handlers.onMessage(second);
    handlers.onMessage(first);
    handle.detach();

    const replayed: AiMessage[] = [];
    manager.attach(sessionKey, { onMessage: (message) => replayed.push(message) });
    await Promise.resolve();

    assert.deepEqual(replayed, [first, second]);
  });
}

test("completing the visible parallel gate surfaces and retains the other gate", async () => {
  let handlers!: ExecutionHubHandlers;
  const manager = new ExecutionSessionManager((value) => {
    handlers = value;
    return createExecutionClient();
  });
  const visible: AiMessage[] = [];
  const handle = manager.attach(sessionKey, { onMessage: (message) => visible.push(message) });
  const first = createGateInteraction("first");
  const second = createGateInteraction("second");
  handlers.onMessage(first);
  handlers.onMessage(second);
  visible.length = 0;

  await handle.submitHumanResponse({
    response: { kind: "workflow-gate", interactionId: "second", approved: true },
  });

  assert.deepEqual(visible, [first]);
  assert.equal(handle.getStatus(), "waiting-approval");
  handle.detach();
  const replayed: AiMessage[] = [];
  const reattached = manager.attach(sessionKey, { onMessage: (message) => replayed.push(message) });
  await Promise.resolve();
  assert.deepEqual(replayed, [first]);
  await reattached.submitHumanResponse({
    response: { kind: "workflow-gate", interactionId: "first", approved: true },
  });
  reattached.detach();
  const afterCompletion: AiMessage[] = [];
  manager.attach(sessionKey, { onMessage: (message) => afterCompletion.push(message) });
  await Promise.resolve();
  assert.deepEqual(afterCompletion, []);
});

test("a terminal message invalidates parallel gates already scheduled for replay", async () => {
  let handlers!: ExecutionHubHandlers;
  const manager = new ExecutionSessionManager((value) => {
    handlers = value;
    return createExecutionClient();
  });
  const handle = manager.attach(sessionKey, { onMessage: () => undefined });
  handlers.onMessage(createGateInteraction("first"));
  handlers.onMessage(createGateInteraction("second"));
  handle.detach();
  const replayed: AiMessage[] = [];
  manager.attach(sessionKey, { onMessage: (message) => replayed.push(message) });
  const terminal = createTurnFinishedMessage();
  handlers.onMessage(terminal);
  await Promise.resolve();
  assert.deepEqual(replayed, [terminal]);
});

test("submission completion does not clear the next queued interaction during replay", async () => {
  let handlers!: ExecutionHubHandlers;
  let finish!: () => void;
  const manager = new ExecutionSessionManager((value) => {
    handlers = value;
    return {
      ...createExecutionClient(),
      submitHumanResponse: () =>
        new Promise<void>((resolve) => {
          finish = resolve;
        }),
    };
  });
  const handle = manager.attach(sessionKey, { onMessage: () => undefined });
  handlers.onMessage(createQuestionInteraction("first"));
  const submitting = handle.submitHumanResponse({
    response: { kind: "user-input", interactionId: "first", cancelled: true },
  });
  const next = createQuestionInteraction("second");
  handlers.onMessage(next);
  finish();
  await submitting;
  handle.detach();
  const replayed: AiMessage[] = [];
  manager.attach(sessionKey, { onMessage: (message) => replayed.push(message) });
  await Promise.resolve();
  assert.deepEqual(replayed, [next]);
});

function createTurnFinishedMessage(): AiMessage {
  return {
    messageId: "turn-finished-1",
    role: "system",
    author: "$agw",
    contents: [],
    additionalProperties: { type: "agw-turn-finished", status: "completed" },
  };
}

test("manager marks a restored durable execution active", async () => {
  const manager = new ExecutionSessionManager(() => ({
    ...createExecutionClient(),
    configure: async () => ({ restoredDurableExecution: true }),
    hasActiveExecution: () => true,
  }));
  const handle = manager.attach(sessionKey, { onMessage: () => undefined });

  const result = await handle.configure({ projectId: "project-1", contextId: "context-1" });

  assert.deepEqual(result, { restoredDurableExecution: true });
  assert.equal(handle.getStatus(), "running");
});

test("manager creates independent clients for different conversation execution keys", () => {
  let createdClientCount = 0;
  const manager = new ExecutionSessionManager(() => {
    createdClientCount += 1;
    return createExecutionClient();
  });
  const otherSessionKey = { ...sessionKey, contextId: "context-2" };
  const first = manager.attach(sessionKey, { onMessage: () => undefined });
  const second = manager.attach(otherSessionKey, { onMessage: () => undefined });

  assert.equal(createdClientCount, 2);
  assert.equal(first.matchesKey(sessionKey), true);
  assert.equal(first.matchesKey(otherSessionKey), false);
  assert.equal(second.matchesKey(otherSessionKey), true);
});

test("manager restores the complete active turn instead of replaying capped deltas", async () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  const first = manager.attach(sessionKey, { onMessage: () => undefined });
  const input = {
    messageId: "user-1",
    author: "$agw",
    contents: [{ type: "TextContent", content: "run" }],
  };

  await first.execute({
    conversationId: "conversation-1",
    agentId: "agent-1",
    agentType: 0,
    input,
  });
  clientHandlers?.onMessage({
    messageId: "turn-start-1",
    role: "system",
    author: "$agw",
    contents: [],
    streamingScopeId: "user-1",
    additionalProperties: { type: "agw-turn-start" },
  });

  const deltas = Array.from({ length: 250 }, (_, index) => String(index % 10));
  for (const content of deltas) {
    clientHandlers?.onMessage({
      messageId: "assistant-1",
      role: "assistant",
      author: "general-agent",
      contents: [{ type: "TextContent", content }],
      streamingScopeId: "user-1",
    });
  }
  const interaction = createQuestionInteraction("active-interaction");
  clientHandlers?.onMessage(interaction);

  first.detach();
  const replayed: AiMessage[] = [];
  const second = manager.attach(sessionKey, { onMessage: (message) => replayed.push(message) });
  await Promise.resolve();

  assert.deepEqual(replayed, [interaction]);
  const snapshot = second.getActiveTurnSnapshot();
  assert.ok(snapshot);
  assert.equal(snapshot.streamingScopeId, "user-1");
  assert.equal(snapshot.messages[0]?.role, "user");
  assert.equal(
    snapshot.messages.find((message) => message.messageId === "assistant-1")?.contents[0]?.content,
    deltas.join(""),
  );

  clientHandlers?.onMessage({
    messageId: "turn-finished-1",
    role: "system",
    author: "$agw",
    contents: [],
    streamingScopeId: "user-1",
    additionalProperties: { type: "agw-turn-finished", status: "completed" },
  });
  assert.equal(second.getActiveTurnSnapshot(), null);
});

test("manager preserves active recovery state when durable subscribe temporarily fails", async () => {
  const failedState: ExecutionReconnectState = {
    status: "failed",
    retryAttempt: 7,
    retryDelayMs: 0,
  };
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return {
      ...createExecutionClient(),
      configure: async () => {
        clientHandlers?.onReconnectFailed?.(failedState);
        throw new Error("temporary subscribe failure");
      },
      hasActiveExecution: () => true,
    };
  });
  const handle = manager.attach(sessionKey, { onMessage: () => undefined });

  await assert.rejects(
    handle.configure({ projectId: "project-1", contextId: "context-1" }),
    /temporary subscribe failure/,
  );

  assert.equal(handle.getStatus(), "running");
  assert.equal(handle.getReconnectState(), failedState);
});

test("manager preserves reconnect state while a conversation is detached", () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  const receivedStates: ExecutionReconnectState[] = [];
  const firstHandle = manager.attach(sessionKey, {
    onMessage: () => undefined,
    onReconnecting: (state) => receivedStates.push(state),
  });
  const reconnectState: ExecutionReconnectState = {
    status: "reconnecting",
    retryAttempt: 3,
    retryDelayMs: 5_000,
  };

  clientHandlers?.onReconnecting?.(reconnectState);

  assert.equal(firstHandle.getReconnectState(), reconnectState);
  assert.deepEqual(receivedStates, [reconnectState]);

  firstHandle.detach();
  const secondHandle = manager.attach(sessionKey, { onMessage: () => undefined });
  assert.equal(secondHandle.getReconnectState(), reconnectState);

  clientHandlers?.onReconnected?.();
  assert.equal(secondHandle.getReconnectState(), null);
});

test("manager keeps reconnect state until the manual retry succeeds", async () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  let retryCount = 0;
  const retryingState: ExecutionReconnectState = {
    status: "reconnecting",
    retryAttempt: 1,
    retryDelayMs: 0,
  };
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return {
      ...createExecutionClient(),
      retryConnection: async () => {
        retryCount += 1;
        clientHandlers?.onReconnecting?.(retryingState);
      },
    };
  });
  const failedState: ExecutionReconnectState = {
    status: "failed",
    retryAttempt: 7,
    retryDelayMs: 0,
  };
  const handle = manager.attach(sessionKey, { onMessage: () => undefined });

  clientHandlers?.onReconnectFailed?.(failedState);

  assert.equal(handle.getReconnectState(), failedState);
  await manager.retryConnection(sessionKey);
  assert.equal(retryCount, 1);
  assert.equal(handle.getReconnectState(), retryingState);

  clientHandlers?.onReconnectFailed?.(failedState);
  assert.equal(handle.getReconnectState(), failedState);

  clientHandlers?.onReconnected?.();
  assert.equal(handle.getReconnectState(), null);
});

test("manager clears a stale active status when reconnect finds no execution", async () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  let activeExecution = true;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return {
      ...createExecutionClient(),
      configure: async () => ({ restoredDurableExecution: true }),
      hasActiveExecution: () => activeExecution,
    };
  });
  const handle = manager.attach(sessionKey, { onMessage: () => undefined });

  await handle.configure({ projectId: "project-1", contextId: "context-1" });
  assert.equal(handle.getStatus(), "running");

  activeExecution = false;
  clientHandlers?.onReconnected?.();

  assert.equal(handle.getStatus(), "idle");
});

test("manager replays an unresolved question interaction when chat reattaches", async () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  const interaction = createQuestionInteraction();
  const initiallyReceived: AiMessage[] = [];
  const firstHandle = manager.attach(sessionKey, {
    onMessage: (message) => initiallyReceived.push(message),
  });

  clientHandlers?.onMessage(interaction);
  assert.deepEqual(initiallyReceived, [interaction]);

  firstHandle.detach();
  const replayed: AiMessage[] = [];
  manager.attach(sessionKey, { onMessage: (message) => replayed.push(message) });
  await Promise.resolve();

  assert.deepEqual(replayed, [interaction]);
});

test("manager clears the replayed question interaction after a response is submitted", async () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  const interaction = createQuestionInteraction();
  const firstHandle = manager.attach(sessionKey, { onMessage: () => undefined });

  clientHandlers?.onMessage(interaction);
  await firstHandle.submitHumanResponse({
    response: {
      kind: "user-input",
      interactionId: "interaction-1",
      cancelled: false,
      responseData: { answers: {} },
    },
  });
  firstHandle.detach();

  const replayed: AiMessage[] = [];
  manager.attach(sessionKey, { onMessage: (message) => replayed.push(message) });
  await Promise.resolve();

  assert.deepEqual(replayed, []);
});

test("manager keeps the question interaction when response submission fails", async () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return {
      ...createExecutionClient(),
      submitHumanResponse: async () => {
        throw new Error("response failed");
      },
    };
  });
  const interaction = createQuestionInteraction();
  const firstHandle = manager.attach(sessionKey, { onMessage: () => undefined });

  clientHandlers?.onMessage(interaction);
  await assert.rejects(
    firstHandle.submitHumanResponse({
      response: {
        kind: "user-input",
        interactionId: "interaction-1",
        cancelled: false,
        responseData: { answers: {} },
      },
    }),
    /response failed/,
  );
  firstHandle.detach();

  const replayed: AiMessage[] = [];
  manager.attach(sessionKey, { onMessage: (message) => replayed.push(message) });
  await Promise.resolve();

  assert.deepEqual(replayed, [interaction]);
});

test("manager drops a buffered question interaction when the turn finishes", async () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  const firstHandle = manager.attach(sessionKey, { onMessage: () => undefined });
  firstHandle.detach();

  clientHandlers?.onMessage(createQuestionInteraction());
  const turnFinished = createTurnFinishedMessage();
  clientHandlers?.onMessage(turnFinished);

  const replayed: AiMessage[] = [];
  manager.attach(sessionKey, { onMessage: (message) => replayed.push(message) });
  await Promise.resolve();

  assert.deepEqual(replayed, [turnFinished]);
});

function createTurnStartMessage(): AiMessage {
  return {
    messageId: "turn-start-1",
    role: "system",
    author: "$agw",
    contents: [],
    additionalProperties: { type: "agw-turn-start" },
  };
}

test("manager notifies once when an active turn finishes", () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  manager.attach(sessionKey, { onMessage: () => undefined });
  const events: { key: typeof sessionKey; status: string }[] = [];
  manager.subscribeTurnFinished((event) => events.push(event));

  clientHandlers?.onMessage(createTurnStartMessage());
  clientHandlers?.onMessage(createTurnFinishedMessage());
  clientHandlers?.onMessage(createTurnFinishedMessage());

  assert.equal(events.length, 1);
  assert.deepEqual(events[0]?.key, sessionKey);
  assert.equal(events[0]?.status, "completed");
});

test("manager notifies with the failed status", () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  manager.attach(sessionKey, { onMessage: () => undefined });
  const events: { status: string }[] = [];
  manager.subscribeTurnFinished((event) => events.push(event));

  clientHandlers?.onMessage(createTurnStartMessage());
  clientHandlers?.onMessage({
    ...createTurnFinishedMessage(),
    additionalProperties: { type: "agw-turn-finished", status: "failed" },
  });

  assert.deepEqual(events, [{ key: sessionKey, status: "failed" }]);
});

test("manager does not notify for a terminal message without an active turn", () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  manager.attach(sessionKey, { onMessage: () => undefined });
  const events: { status: string }[] = [];
  manager.subscribeTurnFinished((event) => events.push(event));

  clientHandlers?.onMessage(createTurnFinishedMessage());

  assert.equal(events.length, 0);
});

test("manager notifies again when a new turn finishes", () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  manager.attach(sessionKey, { onMessage: () => undefined });
  const events: { status: string }[] = [];
  manager.subscribeTurnFinished((event) => events.push(event));

  clientHandlers?.onMessage(createTurnStartMessage());
  clientHandlers?.onMessage(createTurnFinishedMessage());
  clientHandlers?.onMessage(createTurnStartMessage());
  clientHandlers?.onMessage(createTurnFinishedMessage());

  assert.equal(events.length, 2);
});

test("manager stops notifying after the turn finished listener unsubscribes", () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  manager.attach(sessionKey, { onMessage: () => undefined });
  const events: { status: string }[] = [];
  const unsubscribe = manager.subscribeTurnFinished((event) => events.push(event));
  unsubscribe();

  clientHandlers?.onMessage(createTurnStartMessage());
  clientHandlers?.onMessage(createTurnFinishedMessage());

  assert.equal(events.length, 0);
});

test("manager notifies when reconnect finds the execution finished", async () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  let activeExecution = true;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return {
      ...createExecutionClient(),
      configure: async () => ({ restoredDurableExecution: true }),
      hasActiveExecution: () => activeExecution,
    };
  });
  const handle = manager.attach(sessionKey, { onMessage: () => undefined });
  const events: { status: string }[] = [];
  manager.subscribeTurnFinished((event) => events.push(event));

  await handle.configure({ projectId: "project-1", contextId: "context-1" });
  activeExecution = false;
  clientHandlers?.onReconnected?.();

  assert.deepEqual(events, [{ key: sessionKey, status: "completed" }]);
});

function createTurnLifecycleMessage(
  type: "agw-turn-start" | "agw-turn-finished",
  turnId: string,
  status?: string,
): AiMessage {
  return {
    messageId: `${type}-${turnId}`,
    role: "system",
    author: "$agw",
    contents: [],
    additionalProperties: {
      type,
      turnId,
      conversationId: "conversation-1",
      ...(status ? { status } : {}),
    },
  };
}

test("manager records turn statuses of a background conversation after detach", () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  manager.attach(sessionKey, { onMessage: () => undefined }).detach();
  const statuses = () => manager.conversationStatuses.getStatuses(sessionKey).get("conversation-1");

  clientHandlers?.onMessage(createTurnLifecycleMessage("agw-turn-start", "turn-1"));
  const running = statuses();
  clientHandlers?.onMessage(createTurnLifecycleMessage("agw-turn-finished", "turn-1", "failed"));

  assert.equal(running, "running");
  assert.equal(statuses(), "failed");
  assert.equal(
    manager.conversationStatuses.getStatuses({ serverId: "server-a", projectId: "project-2" }).size,
    0,
  );
});

test("manager ignores a finish message that carries only status", () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  manager.attach(sessionKey, { onMessage: () => undefined });
  clientHandlers?.onMessage(createTurnLifecycleMessage("agw-turn-start", "turn-1"));
  const before = manager.conversationStatuses.getStatuses(sessionKey);

  clientHandlers?.onMessage({
    ...createTurnFinishedMessage(),
    additionalProperties: { type: "agw-turn-finished", status: "interrupted" },
  });

  assert.equal(manager.conversationStatuses.getStatuses(sessionKey), before);
  assert.equal(before.get("conversation-1"), "running");
});

function markSuperseded(message: AiMessage): AiMessage {
  return {
    ...message,
    additionalProperties: { ...message.additionalProperties, superseded: true },
  };
}

test("manager keeps snapshot statuses when superseded turn messages are replayed", () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  manager.attach(sessionKey, { onMessage: () => undefined });
  const store = manager.conversationStatuses;
  const status = () => store.getStatuses(sessionKey).get("conversation-1");
  clientHandlers?.onMessage(createTurnLifecycleMessage("agw-turn-start", "turn-a"));

  // B 在另一个客户端成功结束，快照不再包含这个会话；随后回放 A 的结束消息。
  // B completed on another client so the snapshot omits the conversation; A's finish is replayed afterwards.
  store.applySnapshot(sessionKey, store.beginSnapshot(sessionKey), []);
  clientHandlers?.onMessage(
    markSuperseded(createTurnLifecycleMessage("agw-turn-finished", "turn-a", "failed")),
  );
  const afterOldFinish = status();

  // B 仍在运行时，回放 A 的开始与结束消息。A's start and finish are replayed while B is still running.
  store.applySnapshot(sessionKey, store.beginSnapshot(sessionKey), [
    { conversationId: "conversation-1", turnId: "turn-b", status: "running" },
  ]);
  clientHandlers?.onMessage(markSuperseded(createTurnLifecycleMessage("agw-turn-start", "turn-a")));
  clientHandlers?.onMessage(
    markSuperseded(createTurnLifecycleMessage("agw-turn-finished", "turn-a", "interrupted")),
  );

  assert.equal(afterOldFinish, "idle");
  assert.equal(status(), "running");
  assert.equal(store.get(sessionKey, "conversation-1")?.turnId, "turn-b");
});

test("manager notifies subscribers of superseded lifecycle messages with the session key", () => {
  let clientHandlers: ExecutionHubHandlers | undefined;
  const manager = new ExecutionSessionManager((handlers) => {
    clientHandlers = handlers;
    return createExecutionClient();
  });
  manager.attach(sessionKey, { onMessage: () => undefined });
  const notified: (typeof sessionKey)[] = [];
  const unsubscribe = manager.subscribeSupersededTurn((key) => notified.push(key));

  clientHandlers?.onMessage(createTurnLifecycleMessage("agw-turn-start", "turn-a"));
  clientHandlers?.onMessage(
    markSuperseded(createTurnLifecycleMessage("agw-turn-finished", "turn-a", "failed")),
  );
  unsubscribe();
  clientHandlers?.onMessage(markSuperseded(createTurnLifecycleMessage("agw-turn-start", "turn-a")));

  assert.deepEqual(notified, [sessionKey]);
});

test("manager notifies reconnect subscribers after any connection reconnects", () => {
  const handlers: ExecutionHubHandlers[] = [];
  const manager = new ExecutionSessionManager((value) => {
    handlers.push(value);
    return createExecutionClient();
  });
  manager.attach(sessionKey, { onMessage: () => undefined });
  manager.attach({ ...sessionKey, contextId: "context-2" }, { onMessage: () => undefined });
  let reconnects = 0;
  const unsubscribe = manager.subscribeReconnected(() => {
    reconnects += 1;
  });

  for (const handler of handlers) handler.onReconnected?.();
  unsubscribe();
  handlers[0]?.onReconnected?.();

  assert.equal(reconnects, 2);
});

test("manager does not notify when the execute command fails", async () => {
  const manager = new ExecutionSessionManager(() => ({
    ...createExecutionClient(),
    execute: async () => {
      throw new Error("dispatch failed");
    },
  }));
  const handle = manager.attach(sessionKey, { onMessage: () => undefined });
  const events: { status: string }[] = [];
  manager.subscribeTurnFinished((event) => events.push(event));

  await assert.rejects(
    handle.execute({
      conversationId: "conversation-1",
      agentId: "agent-1",
      agentType: 0,
      input: {
        messageId: "user-1",
        author: "$agw",
        contents: [{ type: "TextContent", content: "run" }],
      },
    }),
    /dispatch failed/,
  );

  assert.equal(events.length, 0);
});
