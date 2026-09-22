import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import type { ExecutionHubHandlers, ExecutionSession } from "./execution-session";
import {
  ConversationController,
  type ConversationControllerOptions,
} from "./conversation-controller";

test("history hydration merges reasoning fragments without joining different turns", () => {
  const messages: AiMessage[] = ["first", "second"].flatMap((turn) => [
    {
      messageId: `user-${turn}`,
      role: "user",
      contents: [{ type: "TextContent", content: turn }],
    },
    ...["The", " user", " wants", " me", " to", " fix"].map((content) => ({
      messageId: "thinking",
      role: "assistant",
      author: "claude-code",
      additionalProperties: { modelName: "deepseek-v4-pro" },
      contents: [{ type: "TextReasoningContent", content }],
    })),
    {
      messageId: `result-${turn}`,
      role: "assistant",
      additionalProperties: { type: "result" },
      contents: [{ type: "TextContent", content: "Done" }],
    },
  ]);
  const seed = { revision: 1, conversationId: "conversation", contextId: "context", messages };
  const controller = new ConversationController({
    adapter: { execution: { baseUrl: "https://agw.test", token: null } },
    projectId: "project",
    target: { id: "agent", type: "agent" },
    sessionSeed: seed,
  });
  const checkHistory = () => {
    const snapshot = controller.getSnapshot();
    assert.equal(snapshot.rawMessages.length, 6);
    const summaries = snapshot.items.filter((item) => item.type === "work-summary");
    assert.equal(summaries.length, 2);
    for (const summary of summaries) {
      assert.equal(summary.items.length, 1);
      const item = summary.items[0];
      assert.equal(item.type, "message");
      if (item.type !== "message") return;
      assert.equal(item.message.contents.length, 1);
      assert.deepEqual(item.message.contents[0], {
        type: "reasoning",
        markdown: "The user wants me to fix",
        preview: "The user wants me to fix",
      });
    }
    assert.equal(messages.length, 16, "hydration must not mutate the persisted history");
    assert.equal(messages[1].contents[0].content, "The");
  };
  checkHistory();
  controller.hydrate({ ...seed, revision: 2 });
  checkHistory();
});

for (const status of ["completed", "failed", "interrupted", "recovered"]) {
  test(`work stays visible until ${status}, then folds and survives history hydration`, async () => {
    let handlers!: ExecutionHubHandlers;
    let active = true;
    const controller = new ConversationController({
      adapter: {
        execution: { baseUrl: "https://agw.test", token: null },
        createSession: (value) => {
          handlers = value;
          return {
            configure: async () => ({ restoredDurableExecution: false }),
            execute: async () => undefined,
            hasActiveExecution: () => active,
            dispose: async () => undefined,
          } as unknown as ExecutionSession;
        },
      },
      projectId: "project",
      target: { id: "agent", type: "agent" },
      sessionSeed: {
        revision: 1,
        conversationId: "conversation",
        contextId: "context",
        messages: [],
      },
    });
    const summaries = () =>
      controller.getSnapshot().items.filter((item) => item.type === "work-summary");
    await controller.send("Review changes", []);
    handlers.onMessage({
      messageId: "process",
      role: "assistant",
      contents: [{ type: "TextContent", content: "Reviewing" }],
    });
    handlers.onMessage({
      messageId: "result",
      role: "assistant",
      createdAt: new Date().toISOString(),
      additionalProperties: { type: "result" },
      contents: [{ type: "TextContent", content: "Done" }],
    });
    assert.equal(summaries().length, 0);
    handlers.onClose?.(new Error("Connection lost"));
    assert.equal(summaries().length, 0, "disconnect alone must not hide the process");
    if (status === "recovered") {
      handlers.onReconnecting?.({ status: "reconnecting", retryAttempt: 1, retryDelayMs: 1000 });
      assert.equal(summaries().length, 0);
      active = false;
      handlers.onReconnected?.();
    } else {
      handlers.onMessage({
        messageId: "finished",
        role: "system",
        contents: [],
        additionalProperties: { type: "turn-finished", status },
      });
    }
    assert.equal(summaries().length, 1);
    const summary = summaries()[0];
    const messages = controller.getSnapshot().rawMessages;
    controller.hydrate({
      revision: 2,
      conversationId: "conversation",
      contextId: "context",
      messages,
    });
    assert.equal(summaries()[0].key, summary.key);
    assert.equal(summaries()[0].durationMs, summary.durationMs);
    await controller.dispose();
  });
}

test("equivalent result format options preserve the snapshot and do not notify subscribers", () => {
  const options: ConversationControllerOptions = {
    adapter: { execution: { baseUrl: "https://agw.test", token: null } },
    projectId: "project-1",
    target: { id: "agent-1", type: "agent" },
    sessionSeed: { revision: 1, conversationId: null, contextId: null, messages: [] },
    agentResultFormats: [{ id: "agent-1", resultFormat: "json" }, { id: "agent-2" }],
  };
  const controller = new ConversationController(options);
  const snapshot = controller.getSnapshot();
  let notifications = 0;
  controller.subscribe(() => notifications++);

  for (let index = 0; index < 3; index++) {
    controller.updateOptions({
      ...options,
      agentResultFormats: [
        { id: "AGENT-2", resultFormat: "markdown" },
        { id: "AGENT-1", resultFormat: "json" },
      ],
    });
  }

  assert.equal(notifications, 0);
  assert.equal(controller.getSnapshot(), snapshot);
});

test("in-place result format changes update presentation and notify once", () => {
  const formats: import("@agw/chat-core").AgentResultFormat[] = [
    { id: "agent-1", resultFormat: "markdown" },
  ];
  const options: ConversationControllerOptions = {
    adapter: { execution: { baseUrl: "https://agw.test", token: null } },
    projectId: "project-1",
    target: { id: "agent-1", type: "agent" },
    agentResultFormats: formats,
    sessionSeed: {
      revision: 1,
      conversationId: null,
      contextId: null,
      messages: [
        {
          messageId: "result",
          role: "assistant",
          additionalProperties: { type: "result" },
          contents: [{ type: "TextContent", content: '{"approved":true}' }],
        },
      ],
    },
  };
  const controller = new ConversationController(options);
  let notifications = 0;
  controller.subscribe(() => notifications++);

  formats[0].resultFormat = "json";
  controller.updateOptions(options);
  controller.updateOptions(options);

  assert.equal(notifications, 1);
  const result = controller.getSnapshot().items.find((item) => item.type === "result");
  assert.equal(result?.type === "result" && result.message.contents[0]?.type, "json");
});

test("schema configuration formats live Results and hydrated history, and updates after configuration loads", async () => {
  let handlers!: ExecutionHubHandlers;
  const session = {
    configure: async () => ({ restoredDurableExecution: false }),
    execute: async () => undefined,
    dispose: async () => undefined,
  } as unknown as ExecutionSession;
  const options: ConversationControllerOptions = {
    adapter: {
      execution: { baseUrl: "https://agw.test", token: null },
      createSession: (value) => {
        handlers = value;
        return session;
      },
    },
    projectId: "project-1",
    target: { id: "agent-1", type: "agent" },
    sessionSeed: {
      revision: 1,
      conversationId: "conversation-1",
      contextId: "context-1",
      messages: [],
    },
  };
  const controller = new ConversationController(options);
  const result: AiMessage = {
    messageId: "result",
    role: "assistant",
    author: "codex",
    additionalProperties: { type: "result" },
    contents: [{ type: "TextContent", content: '{"approved":false}' }],
  };
  const content = () => {
    const item = controller.getSnapshot().items.find((item) => item.type === "result");
    return item?.type === "result" ? item.message.contents[0] : undefined;
  };
  await controller.send("Review", []);
  handlers.onMessage(result);
  assert.equal(content()?.type, "markdown");

  const configured: ConversationControllerOptions = {
    ...options,
    agentResultFormats: [{ id: "agent-1", resultFormat: "json" }],
  };
  controller.updateOptions(configured);
  assert.deepEqual(content(), { type: "json", text: '{\n  "approved": false\n}' });

  controller.hydrate({ ...options.sessionSeed, revision: 2, messages: [result] });
  assert.equal(content()?.type, "json");
  controller.updateOptions({
    ...configured,
    agentResultFormats: [{ id: "agent-1", resultFormat: "markdown" }],
  });
  assert.equal(content()?.type, "markdown");
  await controller.dispose();
});

test("controller completes parallel gates individually without server re-publication", async () => {
  let handlers!: ExecutionHubHandlers;
  const submissions: unknown[] = [];
  const session = {
    configure: async () => ({ restoredDurableExecution: false }),
    execute: async () => undefined,
    submitHumanResponse: async (command: unknown) => {
      submissions.push(command);
    },
    dispose: async () => undefined,
  } as unknown as ExecutionSession;
  const controller = new ConversationController({
    adapter: {
      execution: { baseUrl: "https://agw.test", token: null },
      createSession: (value) => {
        handlers = value;
        return session;
      },
    },
    projectId: "project-1",
    target: { id: "flow-1", type: "agentflow" },
    sessionSeed: {
      revision: 1,
      conversationId: "conversation-1",
      contextId: "context-1",
      messages: [],
    },
  });
  await controller.send("Hello", []);
  for (const interactionId of ["first", "second"])
    handlers.onMessage({
      messageId: interactionId,
      role: "system",
      contents: [],
      additionalProperties: {
        type: "interaction-request",
        executionId: "execution-1",
        interaction: {
          kind: "workflow-gate",
          interactionId,
          source: { nodeId: interactionId },
          prompt: "Continue?",
          mode: "approval",
        },
      },
    });
  assert.equal(controller.getSnapshot().pendingInteraction?.interactionId, "second");

  await controller.submitHumanResponse({
    kind: "workflow-gate",
    interactionId: "second",
    approved: true,
  });
  assert.equal(controller.getSnapshot().pendingInteraction?.interactionId, "first");
  await controller.submitHumanResponse({
    kind: "workflow-gate",
    interactionId: "first",
    approved: true,
  });

  assert.equal(controller.getSnapshot().pendingInteraction, null);
  assert.deepEqual(
    submissions,
    ["second", "first"].map((interactionId) => ({
      executionId: "execution-1",
      response: { kind: "workflow-gate", interactionId, approved: true },
    })),
  );
  await controller.dispose();
});

test("response identity rejects stale kinds and preserves a newer pending interaction", async () => {
  let handlers!: ExecutionHubHandlers;
  let finish!: () => void;
  const submissions: unknown[] = [];
  const session = {
    configure: async () => ({ restoredDurableExecution: false }),
    execute: async () => undefined,
    submitHumanResponse: (command: unknown) => {
      submissions.push(command);
      return new Promise<void>((resolve) => {
        finish = resolve;
      });
    },
    dispose: async () => undefined,
  } as unknown as ExecutionSession;
  const controller = new ConversationController({
    adapter: {
      execution: { baseUrl: "https://agw.test", token: null },
      createSession: (value) => {
        handlers = value;
        return session;
      },
    },
    projectId: "project-1",
    target: { id: "agent-1", type: "agent" },
    sessionSeed: {
      revision: 1,
      conversationId: "conversation-1",
      contextId: "context-1",
      messages: [],
    },
  });
  await controller.send("Hello", []);
  const request = (interactionId: string): AiMessage => ({
    messageId: interactionId,
    role: "system",
    contents: [],
    additionalProperties: {
      type: "interaction-request",
      executionId: "request-execution",
      interaction: {
        kind: "user-input",
        interactionId,
        prompt: "Confirm?",
        source: {},
        inputKind: "confirm",
        payload: {},
      },
    },
  });
  handlers.onMessage(request("first"));
  await controller.submitHumanResponse({
    kind: "tool-approval",
    interactionId: "first",
    approved: true,
    scope: "Once",
  });
  await controller.submitHumanResponse({
    kind: "user-input",
    interactionId: "stale",
    cancelled: true,
  });
  assert.deepEqual(submissions, []);

  const response = { kind: "user-input" as const, interactionId: "first", cancelled: true };
  const pending = controller.submitHumanResponse(response);
  handlers.onMessage(request("second"));
  finish();
  await pending;

  assert.deepEqual(submissions, [{ executionId: "request-execution", response }]);
  assert.equal(controller.getSnapshot().pendingInteraction?.interactionId, "second");
  await controller.dispose();
});

test("conversation controller owns raw messages, control state, usage, and render items", async () => {
  let handlers!: ExecutionHubHandlers;
  let announcedConversationId: string | null = null;
  let executionRequest: Parameters<ExecutionSession["execute"]>[0] | null = null;
  const fakeSession = {
    configure: async () => ({ restoredDurableExecution: false }),
    execute: async (request: Parameters<ExecutionSession["execute"]>[0]) => {
      executionRequest = request;
    },
    interrupt: async () => undefined,
    setMode: async () => undefined,
    setPermissionMode: async () => undefined,
    submitHumanResponse: async () => undefined,
    resumeCheckpoint: async () => "execution-2",
    listAgentflowCheckpoints: async () => [],
    dispose: async () => undefined,
  } as unknown as ExecutionSession;
  const controller = new ConversationController({
    adapter: {
      execution: { baseUrl: "https://agw.test", token: "token" },
      createSession: (nextHandlers) => {
        handlers = nextHandlers;
        return fakeSession;
      },
      onConversationIdChange: (conversationId) => {
        announcedConversationId = conversationId;
      },
    },
    projectId: "project-1",
    target: { id: "agent-1", type: "agent" },
    sessionSeed: {
      revision: 1,
      conversationId: null,
      contextId: "context-1",
      messages: [],
    },
  });

  await controller.send("hello", []);
  assert.match(controller.getSnapshot().conversationId ?? "", /^[0-9a-f-]{36}$/u);
  assert.equal(announcedConversationId, controller.getSnapshot().conversationId);
  assert.equal(executionRequest?.conversationId, controller.getSnapshot().conversationId);
  assert.equal(controller.getSnapshot().isExecuting, true);
  assert.equal(controller.getSnapshot().items[0]?.alignment, "right");

  handlers.onMessage({
    messageId: "turn-start",
    role: "system",
    contents: [],
    additionalProperties: { type: "turn-start", streamingScopeId: "user-scope" },
  });
  handlers.onMessage({
    messageId: "assistant-1",
    role: "assistant",
    author: "agent",
    contents: [
      { type: "TextContent", content: "done" },
      { type: "UsageContent", content: { totalTokenCount: 3 } },
    ],
  } as AiMessage);
  assert.equal(controller.getSnapshot().rawMessages.at(-1)?.streamingScopeId, "user-scope");
  assert.equal(controller.getSnapshot().usage.totalTokenCount, 3);
  assert.equal(controller.getSnapshot().items.at(-1)?.type, "message");

  handlers.onMessage({
    messageId: "question",
    role: "system",
    contents: [{ type: "TextContent", content: "Choose" }],
    additionalProperties: {
      type: "interaction-request",
      interaction: {
        kind: "user-input",
        interactionId: "request-1",
        source: {},
        prompt: "Choose",
        inputKind: "questions",
        payload: {
          questions: [
            {
              question: "Choice?",
              header: "Choice",
              multiSelect: false,
              options: [
                { label: "A", description: "A" },
                { label: "B", description: "B" },
              ],
            },
          ],
        },
      },
    },
  });
  assert.equal(controller.getSnapshot().pendingInteraction?.interactionId, "request-1");
  assert.equal(controller.getSnapshot().items.at(-1)?.type, "human-interaction");
});

test("command errors retain an active turn and block another send until recovery confirms idle", async () => {
  let handlers!: ExecutionHubHandlers;
  let active = true;
  let sends = 0;
  const session = {
    configure: async () => ({ restoredDurableExecution: false }),
    hasActiveExecution: () => active,
    execute: async () => {
      sends++;
      throw new Error("Startup response lost");
    },
    interrupt: async () => {
      throw new Error("Stop response lost");
    },
    dispose: async () => undefined,
  } as unknown as ExecutionSession;
  const controller = new ConversationController({
    adapter: {
      execution: { baseUrl: "https://agw.test", token: null },
      createSession: (value) => {
        handlers = value;
        return session;
      },
    },
    projectId: "project",
    target: { id: "agent", type: "agent" },
    sessionSeed: {
      revision: 1,
      conversationId: "conversation",
      contextId: "context",
      messages: [],
    },
  });
  try {
    await controller.send("hello", []);
    assert.equal(controller.getSnapshot().isExecuting, true);
    await controller.stop();
    assert.equal(controller.getSnapshot().isExecuting, true);
    await controller.send("again", []);
    assert.equal(sends, 1);
    handlers.onReconnected?.();
    assert.equal(controller.getSnapshot().isExecuting, true);
    active = false;
    handlers.onReconnected?.();
    assert.equal(controller.getSnapshot().isExecuting, false);
    await controller.send("another message", []);
    assert.equal(sends, 2);
    assert.equal(controller.getSnapshot().isExecuting, false);
  } finally {
    await controller.dispose();
  }
});

test("selecting Full access preserves the current tool approval and consumes server status", async () => {
  let handlers!: ExecutionHubHandlers;
  const session = {
    configure: async () => ({ restoredDurableExecution: false }),
    execute: async () => undefined,
    setPermissionMode: async () =>
      handlers.onMessage({
        messageId: "status",
        contents: [],
        additionalProperties: {
          type: "permission-status",
          activePermissionMode: "alwaysAsk",
          nextPermissionMode: "fullAccess",
          permissionChangePending: true,
        },
      }),
    dispose: async () => undefined,
  } as unknown as ExecutionSession;
  const controller = new ConversationController({
    adapter: {
      execution: { baseUrl: "https://agw.test", token: null },
      createSession: (value) => {
        handlers = value;
        return session;
      },
    },
    projectId: "project",
    target: { id: "agent", type: "agent" },
    permissionMode: "alwaysAsk",
    sessionSeed: {
      revision: 1,
      conversationId: "conversation",
      contextId: "context",
      messages: [],
    },
  });
  await controller.send("Hello", []);
  handlers.onMessage({
    messageId: "pending",
    contents: [],
    additionalProperties: {
      type: "interaction-request",
      interaction: {
        kind: "tool-approval",
        interactionId: "tool",
        prompt: "Allow?",
        source: { toolName: "Bash" },
        arguments: { command: "test" },
      },
    },
  });
  await controller.setPermissionMode("fullAccess");
  assert.equal(controller.getSnapshot().pendingInteraction?.interactionId, "tool");
  assert.equal(controller.getSnapshot().activePermissionMode, "alwaysAsk");
  assert.equal(controller.getSnapshot().permissionMode, "fullAccess");
  assert.equal(controller.getSnapshot().permissionChangePending, true);
  await controller.dispose();
});

test("controller accepts live node inputs and deduplicates replay without creating another turn", async () => {
  let handlers!: ExecutionHubHandlers;
  const session = {
    configure: async () => ({ restoredDurableExecution: false }),
    execute: async () => undefined,
    dispose: async () => undefined,
  } as unknown as ExecutionSession;
  const controller = new ConversationController({
    adapter: {
      execution: { baseUrl: "https://agw.test", token: null },
      createSession: (value) => {
        handlers = value;
        return session;
      },
    },
    projectId: "project-1",
    target: { id: "flow-1", type: "agentflow" },
    sessionSeed: {
      revision: 1,
      conversationId: "conversation-1",
      contextId: "context-1",
      messages: [],
    },
  });
  await controller.send("review", []);
  const user = controller.getSnapshot().rawMessages[0];
  const input: AiMessage = {
    messageId: "node-input",
    role: "user",
    author: "pi",
    contents: [{ type: "TextContent", content: "review result" }],
    additionalProperties: { agentflowInput: true, nodeName: "Review" },
  };
  handlers.onMessage(user);
  handlers.onMessage(input);
  handlers.onMessage(input);
  const messages = controller.getSnapshot().rawMessages;
  assert.equal(messages.length, 2);
  assert.equal(messages[1].streamingScopeId, messages[0].streamingScopeId);
  assert.equal(messages[1].contents[0].content, "review result");
  assert.equal(controller.getSnapshot().isExecuting, true);
  await controller.dispose();
});
