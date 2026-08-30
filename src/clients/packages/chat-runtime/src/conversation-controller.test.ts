import assert from "node:assert/strict";
import test from "node:test";

import type { AiMessage } from "@agw/api";
import type { ExecutionHubHandlers, ExecutionSession } from "./execution-session";
import { ConversationController } from "./conversation-controller";

test("conversation controller owns raw messages, control state, usage, and render items", async () => {
  let handlers!: ExecutionHubHandlers;
  const fakeSession = {
    configure: async () => ({ restoredDurableExecution: false }),
    execute: async () => undefined,
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
    },
    projectId: "project-1",
    target: { id: "agent-1", type: "agent" },
    sessionSeed: { revision: 1, contextId: "context-1", messages: [] },
  });

  await controller.send("hello", []);
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
      type: "human-interaction-request",
      requestId: "request-1",
      interactionKind: "questions",
      prompt: "Choose",
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
  });
  assert.equal(controller.getSnapshot().pendingHumanGate?.requestId, "request-1");
  assert.equal(controller.getSnapshot().items.at(-1)?.type, "human-interaction");
});
