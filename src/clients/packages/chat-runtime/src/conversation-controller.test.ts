import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import type { ExecutionHubHandlers, ExecutionSession } from "./execution-session";
import { ConversationController } from "./conversation-controller";

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
