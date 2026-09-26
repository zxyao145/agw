import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import { createUserMessage } from "@agw/chat-core";
import type { ExecutionHubHandlers, ExecutionReconnectState } from "./execution-hub";
import type { ExecutionSubmission } from "./execution-queue";
import {
  ExecutionStartRejectedError,
  type ExecutionRequest,
  type ExecutionSetting,
} from "./execution-session";
import { ExecutionSessionManager } from "./execution-session-manager";

const sessionKey = {
  serverId: "server-a",
  projectId: "project-1",
  conversationId: "conversation-1",
};

function createSubmission(
  text: string,
  options: { attachments?: ExecutionSubmission["attachments"]; fileCommentCount?: number } = {},
): ExecutionSubmission {
  return {
    target: { agentId: "agent-1", agentType: 0 },
    text,
    attachments: options.attachments ?? [],
    fileCommentCount: options.fileCommentCount ?? 0,
    createMessage: (value, messageId) => ({
      ...createUserMessage(value, options.attachments ?? []),
      messageId,
    }),
  };
}

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

  const result = await handle.configure({
    projectId: "project-1",
    conversationId: "conversation-1",
  });

  assert.deepEqual(result, { restoredDurableExecution: true });
  assert.equal(handle.getStatus(), "running");
});

test("manager creates independent clients for different conversation execution keys", () => {
  let createdClientCount = 0;
  const manager = new ExecutionSessionManager(() => {
    createdClientCount += 1;
    return createExecutionClient();
  });
  const otherSessionKey = { ...sessionKey, conversationId: "conversation-2" };
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
  let userMessageId = "";
  const first = manager.attach(sessionKey, {
    onMessage: () => undefined,
    onSubmissionStarted: (event) => {
      userMessageId = event.message.messageId;
    },
  });

  await first.configure({ projectId: "project-1", conversationId: "conversation-1" });
  assert.equal(first.submit(createSubmission("run")), "started");
  clientHandlers?.onMessage({
    messageId: "turn-start-1",
    role: "system",
    author: "$agw",
    contents: [],
    streamingScopeId: userMessageId,
    additionalProperties: { type: "agw-turn-start" },
  });

  const deltas = Array.from({ length: 250 }, (_, index) => String(index % 10));
  for (const content of deltas) {
    clientHandlers?.onMessage({
      messageId: "assistant-1",
      role: "assistant",
      author: "general-agent",
      contents: [{ type: "TextContent", content }],
      streamingScopeId: userMessageId,
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
  assert.equal(snapshot.streamingScopeId, userMessageId);
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
    streamingScopeId: userMessageId,
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
    handle.configure({ projectId: "project-1", conversationId: "conversation-1" }),
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

  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
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

  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
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
  manager.attach(
    { ...sessionKey, conversationId: "conversation-2" },
    { onMessage: () => undefined },
  );
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
  const returned: string[] = [];
  const handle = manager.attach(sessionKey, {
    onMessage: () => undefined,
    onSubmissionReturned: (event) => returned.push(event.error.message),
  });
  const events: { status: string }[] = [];
  manager.subscribeTurnFinished((event) => events.push(event));

  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  assert.equal(handle.submit(createSubmission("run")), "started");
  await settle();

  assert.deepEqual(returned, ["dispatch failed"]);
  assert.equal(events.length, 0);
});

async function settle(): Promise<void> {
  for (let index = 0; index < 20; index += 1) await Promise.resolve();
}

/**
 * 可控的队列测试环境：execute 记录请求并进入执行，测试按请求写出开始与结束消息。
 * A controllable queue environment: execute records the request and becomes active, and the test writes start and finish messages per request.
 */
function createQueueHarness(
  options: {
    execute?: (request: ExecutionRequest) => Promise<void>;
    configure?: (setting: ExecutionSetting) => Promise<void>;
  } = {},
) {
  let handlers!: ExecutionHubHandlers;
  let active = false;
  const executed: ExecutionRequest[] = [];
  const configured: ExecutionSetting[] = [];
  // 配置与发送按发生顺序记录。Configurations and sends are recorded in the order they happen.
  const commands: string[] = [];
  const interrupts: Array<string | undefined> = [];
  const text = (request: ExecutionRequest | undefined) =>
    request?.input.contents.find((content) => content.type === "TextContent")?.content;
  const manager = new ExecutionSessionManager((value) => {
    handlers = value;
    return {
      ...createExecutionClient(),
      configure: async (setting: ExecutionSetting) => {
        configured.push(setting);
        commands.push(`configure:${JSON.stringify(setting.environmentVariables ?? {})}`);
        await options.configure?.(setting);
        return { restoredDurableExecution: false };
      },
      hasActiveExecution: () => active,
      execute: async (request: ExecutionRequest) => {
        executed.push(request);
        commands.push(`execute:${text(request)}`);
        active = true;
        if (options.execute) {
          try {
            await options.execute(request);
          } catch (error) {
            active = false;
            throw error;
          }
        }
      },
      interrupt: async (reason?: string) => {
        interrupts.push(reason);
      },
    };
  });
  return {
    manager,
    executed,
    configured,
    commands,
    interrupts,
    text,
    get handlers() {
      return handlers;
    },
    setActive(value: boolean) {
      active = value;
    },
    start(request: ExecutionRequest) {
      handlers.onMessage(createTurnLifecycleMessage("agw-turn-start", request.executionId!));
    },
    finish(
      request: ExecutionRequest,
      status: "completed" | "failed" | "interrupted" = "completed",
    ) {
      active = false;
      handlers.onMessage(
        createTurnLifecycleMessage("agw-turn-finished", request.executionId!, status),
      );
    },
  };
}

test("queue sends one entry after each completed turn in submission order", async () => {
  // Arrange
  const harness = createQueueHarness();
  const started: string[] = [];
  const handle = harness.manager.attach(sessionKey, {
    onMessage: () => undefined,
    onSubmissionStarted: (event) => started.push(event.item.text),
  });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });

  // Act
  assert.equal(handle.submit(createSubmission("first")), "started");
  assert.equal(handle.submit(createSubmission("second")), "queued");
  assert.equal(handle.submit(createSubmission("third")), "queued");
  harness.start(harness.executed[0]!);
  harness.finish(harness.executed[0]!);
  await settle();

  // Assert: exactly one entry follows each completion.
  assert.deepEqual(harness.executed.map(harness.text), ["first", "second"]);
  assert.deepEqual(
    handle.getQueue().items.map((item) => item.text),
    ["third"],
  );
  harness.start(harness.executed[1]!);
  harness.finish(harness.executed[1]!);
  await settle();
  assert.deepEqual(harness.executed.map(harness.text), ["first", "second", "third"]);
  assert.deepEqual(started, ["first", "second", "third"]);
  assert.deepEqual(handle.getQueue().items, []);
  assert.equal(new Set(harness.executed.map((request) => request.conversationId)).size, 1);
});

test("queue keeps sending in the background after the conversation is detached", async () => {
  // Arrange
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  handle.submit(createSubmission("first"));
  handle.submit(createSubmission("second"));

  // Act
  handle.detach();
  harness.start(harness.executed[0]!);
  harness.finish(harness.executed[0]!);
  await settle();

  // Assert: the background entry started and the reattached chat restores its active turn.
  assert.deepEqual(harness.executed.map(harness.text), ["first", "second"]);
  const reattached = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  const snapshot = reattached.getActiveTurnSnapshot();
  assert.equal(snapshot?.messages[0]?.messageId, harness.executed[1]!.input.messageId);
  assert.equal(reattached.getStatus(), "running");
});

test("queue waits for configuration before sending the first entry", async () => {
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });

  assert.equal(handle.submit(createSubmission("draft")), "queued");
  assert.deepEqual(harness.executed, []);
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });

  assert.deepEqual(harness.executed.map(harness.text), ["draft"]);
});

test("queue rejects blank text and image-only input but accepts code comments", async () => {
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  const image = {
    id: "image-1",
    name: "a.png",
    mediaType: "image/png" as const,
    size: 1,
    dataUrl: "data:image/png;base64,AA==",
  };

  assert.throws(() => handle.submit(createSubmission("   ")), /Enter a prompt/);
  assert.throws(
    () => handle.submit(createSubmission("", { attachments: [image] })),
    /Enter a prompt/,
  );
  assert.equal(handle.submit(createSubmission("", { fileCommentCount: 1 })), "queued");
  assert.equal(handle.submit(createSubmission("look", { attachments: [image] })), "queued");
  assert.equal(handle.getQueue().items.length, 2);
});

test("queue edits keep order, images and comments, and wait while the head is edited", async () => {
  // Arrange
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  handle.submit(createSubmission("running"));
  handle.submit(createSubmission("head", { fileCommentCount: 2 }));
  handle.submit(createSubmission("tail"));
  const [head, tail] = handle.getQueue().items;

  // Act: the head is being edited when the running turn completes.
  handle.setEditingQueuedItem(head!.id);
  harness.finish(harness.executed[0]!);
  await settle();

  // Assert
  assert.equal(harness.executed.length, 1);
  assert.throws(() => handle.updateQueuedItem(tail!.id, "  "), /Enter a prompt/);
  handle.updateQueuedItem(head!.id, "");
  assert.equal(harness.text(harness.executed[1]), undefined);
  assert.equal(harness.executed.length, 2);
  assert.equal(harness.executed[1]!.input.messageId, head!.id);
  assert.equal(harness.executed[1]!.executionId, head!.executionId);
  assert.deepEqual(
    handle.getQueue().items.map((item) => item.id),
    [tail!.id],
  );
});

test("queue removal drops the entry and cancelling an edit lets the head send", async () => {
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  handle.submit(createSubmission("running"));
  handle.submit(createSubmission("removed"));
  handle.submit(createSubmission("kept"));
  const [removed, kept] = handle.getQueue().items;

  handle.removeQueuedItem(removed!.id);
  handle.setEditingQueuedItem(kept!.id);
  harness.finish(harness.executed[0]!);
  await settle();
  assert.equal(harness.executed.length, 1);
  handle.setEditingQueuedItem(null);

  assert.deepEqual(harness.executed.map(harness.text), ["running", "kept"]);
});

test("switching conversations cancels an unsaved edit so the head can send", async () => {
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  handle.submit(createSubmission("running"));
  handle.submit(createSubmission("edited"));
  handle.setEditingQueuedItem(handle.getQueue().items[0]!.id);
  harness.finish(harness.executed[0]!);
  await settle();

  handle.detach();

  assert.equal(handle.getQueue().editingItemId, null);
  assert.deepEqual(harness.executed.map(harness.text), ["running", "edited"]);
});

for (const status of ["failed", "interrupted"] as const) {
  test(`queue pauses after a ${status} turn, appends new entries, and resumes on request`, async () => {
    // Arrange
    const harness = createQueueHarness();
    const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
    await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
    handle.submit(createSubmission("first"));
    handle.submit(createSubmission("second"));

    // Act
    harness.start(harness.executed[0]!);
    harness.finish(harness.executed[0]!, status);
    await settle();
    assert.equal(handle.submit(createSubmission("third")), "queued");

    // Assert
    assert.equal(harness.executed.length, 1);
    assert.equal(handle.getQueue().paused, true);
    assert.deepEqual(
      handle.getQueue().items.map((item) => item.text),
      ["second", "third"],
    );
    handle.resumeQueue();
    assert.deepEqual(harness.executed.map(harness.text), ["first", "second"]);
    assert.equal(handle.getQueue().paused, false);
  });
}

test("a confirmed start rejection keeps the entry editable at the head and pauses", async () => {
  // Arrange
  let reject = true;
  const harness = createQueueHarness({
    execute: async () => {
      if (reject) throw new ExecutionStartRejectedError(new Error("HubException: 4090021: busy"));
    },
  });
  const returned: string[] = [];
  const removedMessages: string[] = [];
  const handle = harness.manager.attach(sessionKey, {
    onMessage: () => undefined,
    onSubmissionReturned: (event) => {
      returned.push(event.item.text);
      removedMessages.push(event.item.id);
    },
  });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });

  // Act
  handle.submit(createSubmission("first"));
  handle.submit(createSubmission("second"));
  await settle();

  // Assert
  const head = handle.getQueue().items[0]!;
  assert.equal(head.text, "first");
  assert.equal(head.uncertain, false);
  assert.equal(handle.getQueue().paused, true);
  assert.deepEqual(returned, ["first"]);
  assert.deepEqual(removedMessages, [head.id]);
  handle.updateQueuedItem(head.id, "first, edited");
  reject = false;
  handle.resumeQueue();
  assert.equal(harness.text(harness.executed.at(-1)), "first, edited");
  assert.equal(harness.executed.at(-1)!.executionId, head.executionId);
});

test("stop clears the queue before interrupting and rejects new entries until the turn ends", async () => {
  // Arrange
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  handle.submit(createSubmission("running"));
  handle.submit(createSubmission("pending"));
  harness.start(harness.executed[0]!);

  // Act
  await handle.stop("Stop requested by user.");

  // Assert
  assert.deepEqual(handle.getQueue().items, []);
  assert.equal(handle.getQueue().stopping, true);
  assert.deepEqual(harness.interrupts, ["Stop requested by user."]);
  assert.throws(
    () => handle.submit(createSubmission("too early")),
    /wait for the current execution/,
  );
  harness.finish(harness.executed[0]!, "interrupted");
  await settle();
  assert.equal(handle.getQueue().stopping, false);
  assert.equal(handle.getQueue().paused, false);
  assert.deepEqual(harness.executed.map(harness.text), ["running"]);
  assert.equal(handle.submit(createSubmission("after stop")), "started");
});

test("stop during a send that has not started cancels the queue and interrupts that send", async () => {
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  handle.submit(createSubmission("dispatching"));
  handle.submit(createSubmission("pending"));

  await handle.stop();
  harness.start(harness.executed[0]!);
  harness.finish(harness.executed[0]!, "interrupted");
  await settle();

  assert.deepEqual(harness.executed.map(harness.text), ["dispatching"]);
  assert.equal(harness.interrupts.length, 1);
  assert.deepEqual(handle.getQueue().items, []);
});

test("duplicate and foreign finish messages do not send extra entries", async () => {
  // Arrange
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  handle.submit(createSubmission("first"));
  handle.submit(createSubmission("second"));
  handle.submit(createSubmission("third"));
  harness.start(harness.executed[0]!);

  // Act: a finish message of another turn arrives while the first one runs, then the first finishes twice.
  harness.handlers.onMessage(
    createTurnLifecycleMessage("agw-turn-finished", "other-turn", "completed"),
  );
  await settle();
  assert.equal(harness.executed.length, 1);
  harness.finish(harness.executed[0]!);
  await settle();
  harness.handlers.onMessage(
    createTurnLifecycleMessage("agw-turn-finished", harness.executed[0]!.executionId!, "completed"),
  );
  await settle();

  // Assert
  assert.deepEqual(harness.executed.map(harness.text), ["first", "second"]);
  assert.deepEqual(
    handle.getQueue().items.map((item) => item.text),
    ["third"],
  );
});

test("a reconnect without an active execution pauses instead of sending the next entry", async () => {
  // Arrange
  const harness = createQueueHarness();
  const returned: boolean[] = [];
  const handle = harness.manager.attach(sessionKey, {
    onMessage: () => undefined,
    onSubmissionReturned: (event) => returned.push(event.item.uncertain),
  });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  handle.submit(createSubmission("unconfirmed"));
  handle.submit(createSubmission("waiting"));

  // Act: the start message never arrived and the reconnected server reports no active execution.
  harness.setActive(false);
  harness.handlers.onReconnected?.();
  await settle();

  // Assert: the unconfirmed entry returns locked, keeps its execution ID, and nothing else is sent.
  assert.equal(harness.executed.length, 1);
  const [head, next] = handle.getQueue().items;
  assert.equal(head!.text, "unconfirmed");
  assert.equal(head!.uncertain, true);
  assert.equal(head!.executionId, harness.executed[0]!.executionId);
  assert.equal(next!.text, "waiting");
  assert.equal(handle.getQueue().paused, true);
  assert.deepEqual(returned, [true]);
  assert.throws(() => handle.setEditingQueuedItem(head!.id), /cannot be edited/);
});

test("a started turn whose outcome is lost is consumed and the rest of the queue pauses", async () => {
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  handle.submit(createSubmission("started"));
  handle.submit(createSubmission("waiting"));
  harness.start(harness.executed[0]!);

  harness.setActive(false);
  harness.handlers.onClose?.(new Error("connection lost"));
  await settle();

  assert.deepEqual(
    handle.getQueue().items.map((item) => item.text),
    ["waiting"],
  );
  assert.equal(handle.getQueue().paused, true);
  assert.equal(harness.executed.length, 1);
});

test("disposing a conversation clears its queue", async () => {
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  handle.submit(createSubmission("running"));
  handle.submit(createSubmission("pending"));
  const version = harness.manager.getQueueVersion();

  await harness.manager.discard(sessionKey);

  assert.deepEqual(harness.manager.getQueue(sessionKey).items, []);
  assert.ok(harness.manager.getQueueVersion() > version);
  assert.equal(harness.manager.has(sessionKey), false);
});

test("a superseded finish of the in-flight entry ends its wait and pauses the rest", async () => {
  // Arrange
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  handle.submit(createSubmission("first"));
  handle.submit(createSubmission("second"));
  handle.submit(createSubmission("third"));
  harness.start(harness.executed[0]!);

  // Act: another client started a newer turn, so the first entry's only finish message is superseded.
  harness.setActive(false);
  harness.handlers.onMessage(
    markSuperseded(
      createTurnLifecycleMessage(
        "agw-turn-finished",
        harness.executed[0]!.executionId!,
        "completed",
      ),
    ),
  );
  await settle();

  // Assert
  assert.equal(harness.executed.length, 1);
  assert.equal(handle.getQueue().paused, true);
  assert.match(handle.getQueue().pauseReason ?? "", /newer message/);
  assert.deepEqual(
    handle.getQueue().items.map((item) => item.text),
    ["second", "third"],
  );
  handle.resumeQueue();
  assert.deepEqual(harness.executed.map(harness.text), ["first", "second"]);
});

test("a superseded finish that arrives after reconnecting lets the next submission start", async () => {
  // Arrange
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  handle.submit(createSubmission("first"));
  harness.start(harness.executed[0]!);

  // Act: the connection reconnects while the execution is still active, then the replayed finish is superseded.
  harness.handlers.onReconnected?.();
  await settle();
  harness.setActive(false);
  harness.handlers.onMessage(
    markSuperseded(
      createTurnLifecycleMessage(
        "agw-turn-finished",
        harness.executed[0]!.executionId!,
        "completed",
      ),
    ),
  );
  await settle();

  // Assert
  assert.equal(handle.getQueue().paused, false);
  assert.equal(handle.submit(createSubmission("second")), "started");
  assert.deepEqual(harness.executed.map(harness.text), ["first", "second"]);
});

test("a superseded finish after a stop ends the stop so new entries are accepted", async () => {
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure({ projectId: "project-1", conversationId: "conversation-1" });
  handle.submit(createSubmission("running"));
  harness.start(harness.executed[0]!);
  await handle.stop();

  harness.setActive(false);
  harness.handlers.onMessage(
    markSuperseded(
      createTurnLifecycleMessage(
        "agw-turn-finished",
        harness.executed[0]!.executionId!,
        "interrupted",
      ),
    ),
  );
  await settle();

  assert.equal(handle.getQueue().stopping, false);
  assert.equal(handle.submit(createSubmission("after stop")), "started");
});

const baseSetting: ExecutionSetting = {
  projectId: "project-1",
  conversationId: "conversation-1",
  environmentVariables: { MODE: "old" },
  permissionMode: "fullAccess",
  resultOnly: false,
};

test("a settings change during a running turn applies after it ends and before the next entry", async () => {
  // Arrange
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure(baseSetting);
  handle.submit(createSubmission("first"));
  harness.start(harness.executed[0]!);

  // Act: the environment variables change while the first turn runs, then the next entry is submitted.
  const changed = { ...baseSetting, environmentVariables: { MODE: "new" } };
  const result = await handle.configure(changed);
  assert.equal(handle.submit(createSubmission("second")), "queued");
  assert.deepEqual(harness.configured, [baseSetting]);
  harness.finish(harness.executed[0]!);
  await settle();

  // Assert: the new settings were sent only after the turn ended, and before the next entry.
  assert.deepEqual(result, { restoredDurableExecution: false });
  assert.deepEqual(harness.configured, [baseSetting, changed]);
  assert.deepEqual(harness.commands, [
    'configure:{"MODE":"old"}',
    "execute:first",
    'configure:{"MODE":"new"}',
    "execute:second",
  ]);
});

test("a resultOnly change during a running turn also waits for the turn to end", async () => {
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure(baseSetting);
  handle.submit(createSubmission("first"));
  harness.start(harness.executed[0]!);

  await handle.configure({ ...baseSetting, resultOnly: true });
  handle.submit(createSubmission("second"));
  harness.finish(harness.executed[0]!);
  await settle();

  assert.deepEqual(
    harness.configured.map((setting) => setting.resultOnly),
    [false, true],
  );
  assert.deepEqual(harness.commands, [
    'configure:{"MODE":"old"}',
    "execute:first",
    'configure:{"MODE":"old"}',
    "execute:second",
  ]);
});

test("unchanged settings during a running turn are sent at once", async () => {
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure(baseSetting);
  handle.submit(createSubmission("first"));
  harness.start(harness.executed[0]!);

  await handle.configure({ ...baseSetting, environmentVariables: { MODE: "old" } });

  assert.equal(harness.configured.length, 2);
  assert.equal(handle.getStatus(), "running");
});

test("the queue waits while settings are being applied", async () => {
  // Arrange
  let finishConfigure!: () => void;
  let pending = false;
  const harness = createQueueHarness({
    configure: async () => {
      if (!pending) return;
      await new Promise<void>((resolve) => {
        finishConfigure = resolve;
      });
    },
  });
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure(baseSetting);

  // Act: the connection is idle and a new configuration is still in progress when the entry is submitted.
  pending = true;
  const configuring = handle.configure({ ...baseSetting, environmentVariables: { MODE: "new" } });
  const result = handle.submit(createSubmission("waits"));
  await settle();

  // Assert
  assert.equal(result, "queued");
  assert.deepEqual(harness.executed, []);
  finishConfigure();
  await configuring;
  assert.deepEqual(harness.commands, [
    'configure:{"MODE":"old"}',
    'configure:{"MODE":"new"}',
    "execute:waits",
  ]);
});

test("a failed settings change keeps the queue waiting until it is resumed", async () => {
  // Arrange
  let failures = 0;
  const harness = createQueueHarness({
    configure: async (setting) => {
      if (setting.environmentVariables?.MODE === "new" && failures < 1) {
        failures += 1;
        throw new Error("HubException: 4000001: Invalid params.");
      }
    },
  });
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure(baseSetting);
  handle.submit(createSubmission("first"));
  harness.start(harness.executed[0]!);
  await handle.configure({ ...baseSetting, environmentVariables: { MODE: "new" } });
  handle.submit(createSubmission("second"));

  // Act: applying the new settings after the turn fails.
  harness.finish(harness.executed[0]!);
  await settle();

  // Assert: nothing is sent with the previous settings until the queue is resumed.
  assert.deepEqual(harness.executed.map(harness.text), ["first"]);
  assert.equal(handle.getQueue().paused, true);
  assert.match(handle.getQueue().pauseReason ?? "", /Invalid params/);
  handle.resumeQueue();
  await settle();
  assert.deepEqual(harness.commands.slice(-2), ['configure:{"MODE":"new"}', "execute:second"]);
});

test("a permission mode change keeps the recorded settings in step with the server", async () => {
  const harness = createQueueHarness();
  const handle = harness.manager.attach(sessionKey, { onMessage: () => undefined });
  await handle.configure(baseSetting);
  handle.submit(createSubmission("first"));
  harness.start(harness.executed[0]!);

  // The permission mode changes during the turn, then the same settings arrive with it.
  await handle.setPermissionMode("alwaysAsk");
  await handle.configure({ ...baseSetting, permissionMode: "alwaysAsk" });

  assert.deepEqual(
    harness.configured.map((setting) => setting.permissionMode),
    ["fullAccess", "alwaysAsk"],
  );
});
