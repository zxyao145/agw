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
    setMode: async () => undefined,
    setPermissionMode: async () => undefined,
    interrupt: async () => undefined,
    interruptAndWait: async () => undefined,
    submitHumanResponse: async () => undefined,
    retryConnection: async () => undefined,
    dispose: async () => undefined,
  };
}

function createQuestionInteraction(requestId = "interaction-1"): AiMessage {
  return {
    messageId: `message-${requestId}`,
    role: "system",
    author: "$agw",
    contents: [{ type: "TextContent", content: "Choose before continuing." }],
    additionalProperties: {
      type: "human-interaction-request",
      requestId,
      interactionKind: "questions",
      toolName: "ask_user_question",
      callId: "call-1",
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
  };
}

function createTurnFinishedMessage(): AiMessage {
  return {
    messageId: "turn-finished-1",
    role: "system",
    author: "$agw",
    contents: [],
    additionalProperties: { type: "turn-finished", status: "completed" },
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

  await first.execute({ agentId: "agent-1", agentType: 0, input });
  clientHandlers?.onMessage({
    messageId: "turn-start-1",
    role: "system",
    author: "$agw",
    contents: [],
    streamingScopeId: "user-1",
    additionalProperties: { type: "turn-start" },
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
    additionalProperties: { type: "turn-finished", status: "completed" },
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
  await firstHandle.submitHumanResponse({ requestId: "interaction-1", approved: true });
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
    firstHandle.submitHumanResponse({ requestId: "interaction-1", approved: true }),
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
