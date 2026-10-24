import assert from "node:assert/strict";
import test from "node:test";
import {
  installLayoutMetrics,
  replaceExecutionHub,
  setupDomEnvironment,
  startApiServer,
} from "@agw/test-harness";

import type { AiMessage } from "@agw/api";

const environment = await setupDomEnvironment();
const { React, act, fireEvent, render, screen, waitFor, within } = environment;
installLayoutMetrics(environment.window);
const { configureApiRuntime } = await import("@agw/api");
const { chatHttpClient, configureExecutionRuntime } = await import("@agw/chat-runtime");
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const { Chat } = await import("./chat.tsx");

const scope = { serverId: "browser", projectId: "project-1", conversationId: "conversation-1" };

const HISTORY: AiMessage[] = [
  {
    messageId: "user-1",
    role: "user",
    contents: [{ type: "TextContent", content: "Plan the trip" }],
  },
  {
    messageId: "result-1",
    role: "assistant",
    additionalProperties: { type: "result", turnId: "turn-0" },
    contents: [{ type: "TextContent", content: "Done" }],
  },
];

function details(total: number) {
  return {
    projectId: "project-1",
    conversationId: "conversation-1",
    contextId: "context-1",
    title: "Trip",
    executionCount: 1,
    messageCount: 2,
    createTime: "2026-10-23T08:00:00Z",
    usage: {
      inputTokenCount: total,
      outputTokenCount: 0,
      totalTokenCount: total,
      cachedInputTokenCount: 0,
      reasoningTokenCount: 0,
      unsettledTurnIds: [],
    },
    resumeState: null,
  };
}

function turnMessage(
  type: "agw-turn-start" | "agw-turn-finished",
  turnSequence: number,
): AiMessage {
  return {
    messageId: `turn-1-${type}`,
    role: "system",
    author: "$agw",
    contents: [],
    additionalProperties: {
      type,
      turnId: "turn-1",
      turnSequence,
      conversationId: "conversation-1",
      streamingScopeId: "turn-1",
      ...(type === "agw-turn-finished" ? { status: "completed" } : {}),
    },
  };
}

function usageMessage(turnSequence: number, total: number): AiMessage {
  return {
    messageId: `turn-1-usage-${turnSequence}`,
    role: "assistant",
    author: "agent",
    contents: [{ type: "UsageContent", content: { totalTokenCount: total } }],
    additionalProperties: { turnId: "turn-1", turnSequence, streamingScopeId: "turn-1" },
  };
}

function totalText(): string {
  const aside = screen.getByLabelText("Current conversation details");
  return within(aside).getByText("Total").nextElementSibling?.textContent ?? "";
}

test("ChatAside shows the baseline, then pushed usage, then the synced value, and keeps it after a checkpoint resume", async (t) => {
  const hubs = replaceExecutionHub(t, {
    provider: "InProcess",
    onInvoke: ({ method }) =>
      method === "GetAgentflowCheckpoints"
        ? [
            {
              occurrenceId: "checkpoint-1",
              agentflowId: "flow-1",
              boundarySequence: 1,
              available: true,
              markers: [],
            },
          ]
        : undefined,
  });
  let total = 100;
  const server = await startApiServer({
    "GET /api/projects/project-1/conversations/conversation-1": () => details(total),
    "GET /api/agents/permission-capabilities": { supportedPermissionModes: ["fullAccess"] },
  });
  configureApiRuntime({ baseUrl: server.baseUrl, token: "token-1" });
  configureExecutionRuntime({ baseUrl: server.baseUrl, token: "token-1", attachmentStore: null });
  await chatHttpClient.getConversation(scope);

  render(
    React.createElement(
      QueryClientProvider,
      { client: new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } }) },
      React.createElement(Chat, {
        target: { id: "flow-1", type: "agentflow" },
        projectId: "project-1",
        conversationId: "conversation-1",
        restoreExecution: true,
        sessionSeed: {
          revision: 1,
          conversationId: "conversation-1",
          messages: HISTORY,
          historyTurns: [],
          olderMessagesCursor: null,
          hasOlderMessages: false,
          agentMode: null,
        },
      }),
    ),
  );

  assert.equal(totalText(), "100");
  await waitFor(() =>
    assert.ok(hubs[0]?.invocations.some((call) => call.method === "GetAgentflowCheckpoints")),
  );
  const hub = hubs[0]!;

  await act(async () => {
    hub.receive(turnMessage("agw-turn-start", 1));
    hub.receive(usageMessage(2, 7));
  });
  assert.equal(totalText(), "107");

  total = 112;
  await act(async () => hub.receive(turnMessage("agw-turn-finished", 3)));
  await waitFor(() => assert.equal(totalText(), "112"));

  const resume = await waitFor(() => {
    const button = screen.getByRole("button", { name: /Resume/ });
    assert.equal((button as HTMLButtonElement).disabled, false);
    return button;
  });
  await act(async () => fireEvent.click(resume));
  await waitFor(() => assert.equal(hub.commands("ResumeCheckpointCommand").length, 1));

  assert.equal(totalText(), "112");
});
