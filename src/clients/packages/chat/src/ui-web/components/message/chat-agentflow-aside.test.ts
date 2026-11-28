import assert from "node:assert/strict";
import test, { type TestContext } from "node:test";
import {
  installLayoutMetrics,
  replaceExecutionHub,
  setupDomEnvironment,
  startApiServer,
  type TestHubOptions,
} from "@agw/test-harness";
import type { AiMessage } from "@agw/api";
import type { ChatProps } from "./chat";

const environment = await setupDomEnvironment();
const { React, act, render, screen, waitFor, within } = environment;
installLayoutMetrics(environment.window);
const { configureApiRuntime } = await import("@agw/api");
const { configureExecutionRuntime, executionSessionManager } = await import("@agw/chat-runtime");
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const { Chat } = await import("./chat.tsx");

const flow = { id: "flow-1", type: "agentflow" } as const;
const agent = { id: "agent-1", type: "agent" } as const;
const catalog = [{ id: flow.id, name: "Drafting Flow", publishedRevision: 1 }];
const waitOptions = { timeout: 5_000 };

async function fixture(
  t: TestContext,
  {
    target = flow,
    saved = true,
    onInvoke,
    onExecutionStatus,
  }: {
    target?: ChatProps["target"];
    saved?: boolean;
    onInvoke?: TestHubOptions["onInvoke"];
    onExecutionStatus?: ChatProps["onExecutionStatus"];
  } = {},
) {
  const conversationId = saved ? crypto.randomUUID() : null;
  const sessionKey = conversationId
    ? { serverId: "browser", projectId: "project-1", conversationId }
    : null;
  const hubs = replaceExecutionHub(t, { provider: "InProcess", onInvoke });
  const server = await startApiServer({
    "GET /api/agents": [],
    "GET /api/agentflows": catalog,
    "GET /api/agentflows/detail": {
      agentflow: catalog[0],
      revision: 1,
      definition: {
        nodes: [
          { nodeId: "input", kind: "input", name: null, position: { x: 0, y: 0 } },
          { nodeId: "writer", kind: "agent", name: "Writer", position: { x: 1, y: 0 } },
        ],
        edges: [{ sourceNodeId: "input", targetNodeId: "writer" }],
      },
    },
    "GET /api/agents/permission-capabilities": { supportedPermissionModes: ["fullAccess"] },
    "GET /api/agents/input-capabilities": { supportsSteer: false },
  });
  configureApiRuntime({ baseUrl: server.baseUrl, token: "token-1" });
  configureExecutionRuntime({ baseUrl: server.baseUrl, token: "token-1", attachmentStore: null });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  const sessionSeed = {
    revision: 1,
    conversationId,
    messages: [],
    historyTurns: [],
    olderMessagesCursor: null,
    hasOlderMessages: false,
    agentMode: null,
  };
  const chat = (selectedTarget: ChatProps["target"]) =>
    React.createElement(
      QueryClientProvider,
      { client },
      React.createElement(Chat, {
        target: selectedTarget,
        projectId: "project-1",
        conversationId,
        restoreExecution: true,
        sessionSeed,
        onExecutionStatus,
      }),
    );
  const view = render(chat(target));
  t.after(async () => {
    view.unmount();
    if (sessionKey) await executionSessionManager.discard(sessionKey);
    client.clear();
    await server.close();
  });

  let sequence = 0;
  async function receive(...events: Record<string, unknown>[]) {
    await waitFor(
      () =>
        assert.ok(hubs[0]?.invocations.some(({ method }) => method === "FindInProcessExecution")),
      waitOptions,
    );
    await act(async () => {
      for (const event of events) {
        sequence++;
        const message: AiMessage = {
          messageId: `${event.type}-${sequence}`,
          role: "system",
          author: "$agw",
          contents: [],
          additionalProperties: {
            turnId: "turn-1",
            conversationId,
            turnSequence: sequence,
            ...event,
          },
        };
        hubs[0]!.receive(message);
      }
    });
  }

  return { receive, sessionKey, select: (next: ChatProps["target"]) => view.rerender(chat(next)) };
}

const turnStart = {
  type: "agw-turn-start",
  agentId: flow.id,
  agentType: "agentflow",
  agentflowRevision: 1,
};
const completed = { type: "agentflow-node-status", nodeId: "input", status: "completed" };
const running = { type: "agentflow-node-status", nodeId: "writer", status: "running" };

test("an Agentflow target shows its nodes and an Agent target does not", async (t) => {
  const { select } = await fixture(t, { target: agent, saved: false });
  assert.equal(screen.queryByRole("region", { name: "Agentflow nodes" }), null);
  select(flow);
  assert.ok(await screen.findByTitle("Writer", undefined, waitOptions));
});

test("node statuses move the highlight", async (t) => {
  const { receive } = await fixture(t);
  await screen.findByTitle("Writer", undefined, waitOptions);
  await receive(turnStart, { ...completed, status: "running" });
  assert.ok(screen.getByTitle("Input").closest('[aria-current="step"]'));
  await receive(completed, running);
  assert.equal(screen.getByTitle("Input").closest('[aria-current="step"]'), null);
  assert.ok(screen.getByTitle("Writer").closest('[aria-current="step"]'));
});

test("an interrupted turn clears highlights and keeps completed nodes", async (t) => {
  const { receive } = await fixture(t);
  await screen.findByTitle("Writer", undefined, waitOptions);
  await receive(turnStart, completed, running);
  await receive({ type: "agw-turn-finished", status: "interrupted" });
  const card = screen.getByRole("region", { name: "Agentflow nodes" });
  assert.equal(card.querySelector('[aria-current="step"]'), null);
  const input = screen.getByTitle("Input").closest("li");
  assert.ok(input);
  assert.ok(within(input).getByLabelText("Completed"));
});

test("a running Agentflow stays visible after the selector changes to an Agent", async (t) => {
  const { receive, select } = await fixture(t);
  await screen.findByTitle("Writer", undefined, waitOptions);
  await receive(turnStart, running);
  select(agent);
  assert.ok(screen.getByTitle("Writer").closest('[aria-current="step"]'));
});

test("a recovered node snapshot updates only the sidebar", async (t) => {
  const reported: string[] = [];
  const { sessionKey, receive } = await fixture(t, {
    target: agent,
    onInvoke: ({ method }) => {
      if (method === "FindInProcessExecution") return "previous-connection";
      if (method === "RecoverInProcessExecution") return true;
      if (method === "GetAgentflowNodeStatuses")
        return {
          turnId: "turn-1",
          agentflowId: flow.id,
          agentflowRevision: 1,
          statuses: [running],
        };
    },
    onExecutionStatus: ({ type }) => reported.push(type),
  });
  assert.ok(sessionKey);
  await waitFor(() => {
    assert.equal(
      executionSessionManager.getAgentflowNodeRun(sessionKey).statusByNodeId.writer,
      "running",
    );
  }, waitOptions);
  const writer = await screen.findByTitle("Writer", undefined, waitOptions);
  assert.ok(writer.closest('[aria-current="step"]'));
  assert.deepEqual(reported, []);
  await receive(completed);
  assert.deepEqual(reported, ["agentflow-node-status"]);
});
