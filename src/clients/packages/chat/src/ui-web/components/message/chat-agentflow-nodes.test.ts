import assert from "node:assert/strict";
import test, { type TestContext } from "node:test";
import { setupDomEnvironment, startApiServer, type ApiRequestRecord } from "@agw/test-harness";
import type { AgentflowNodeRunState } from "@agw/chat-core";

const { React, window, render, screen, within, waitFor } = await setupDomEnvironment();
const { configureApiRuntime, resetApiRuntime } = await import("@agw/api");
const { EMPTY_AGENTFLOW_NODE_RUN_STATE } = await import("@agw/chat-core");
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const { ChatAgentflowNodes } = await import("./chat-agentflow-nodes.tsx");
const waitOptions = { timeout: 5_000 };
const findTitle = (title: string) => screen.findByTitle(title, undefined, waitOptions);

const longName = "工作流程节点".repeat(10);
const node = (nodeId: string, kind: string, name: string | null = null) => ({
  nodeId,
  kind,
  name,
  position: { x: 0, y: 0 },
  routingStrategy: "broadcast",
});
const nodes = [
  { ...node("b", "agent", "Writer"), relateId: "writer", instructions: null },
  node("a", "input"),
  { ...node("agent", "agent"), relateId: "writer", instructions: null },
  { ...node("agent-name", "agent"), relateId: "reviewer", instructions: null },
  { ...node("nested", "workflowAsAgent"), relateId: "nested-flow", instructions: null },
  { ...node("checkpoint", "checkpointMarker"), checkpointName: "Draft ready" },
  { ...node("long", "promptAdapter", longName), instructions: null },
  {
    ...node("block", "concurrentBlock"),
    instructions: null,
    members: ["Writer member", "Reviewer member"].map((name, index) => ({
      memberId: String(index),
      kind: "agent",
      relateId: null,
      name,
      instructions: null,
      isManager: false,
    })),
  },
];
const nodeIdsInOrder = ["a", "b", ...nodes.slice(2).map(({ nodeId }) => nodeId)];

const api = await startApiServer({
  "GET /api/agentflows/detail": ({ query }: ApiRequestRecord) => {
    assert.equal(query.get("id"), "flow");
    assert.equal(query.get("revision"), "3");
    return {
      revision: 3,
      definition: {
        nodes,
        edges: nodeIdsInOrder.slice(0, -1).map((nodeId, index) => ({
          edgeId: `edge-${index}`,
          sourceNodeId: nodeId,
          targetNodeId: nodeIdsInOrder[index + 1],
          conditions: [],
        })),
      },
    };
  },
  "GET /api/agents": [
    { id: "writer", name: "writer", displayName: "Reference Writer" },
    { id: "reviewer", name: "Reference Reviewer", displayName: "" },
  ],
  "GET /api/agentflows": [
    { id: "flow", name: "Example Flow" },
    { id: "nested-flow", name: "Nested Flow" },
  ],
});
configureApiRuntime({ baseUrl: api.baseUrl, token: null });
test.after(() => resetApiRuntime());

function showNodes(t: TestContext, state: Partial<AgentflowNodeRunState> = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  t.after(() => client.clear());
  const card = (next: Partial<AgentflowNodeRunState>) =>
    React.createElement(
      QueryClientProvider,
      { client },
      React.createElement(ChatAgentflowNodes, {
        agentflowId: "flow",
        revision: 3,
        run: { ...EMPTY_AGENTFLOW_NODE_RUN_STATE, ...next },
      }),
    );
  const view = render(card(state));
  return (next: Partial<AgentflowNodeRunState>) => view.rerender(card(next));
}

function nodeRow(title: string) {
  const row = screen.getByTitle(title).closest("li");
  assert.ok(row);
  return row;
}

test("nodes appear in execution order with their kind labels", async (t) => {
  showNodes(t);
  await findTitle("Writer");
  const list = screen.getByRole("region", { name: "Agentflow nodes" }).querySelector("ol");
  assert.ok(list);
  assert.deepEqual(
    Array.from(list.children)
      .slice(0, 2)
      .map((row) => row.querySelector("[title]")?.getAttribute("title")),
    ["Input", "Writer"],
  );
  assert.equal(nodeRow("Input").textContent, "Input");
  assert.ok(within(nodeRow("Writer")).getByText("Agent"));
  assert.ok(await screen.findByRole("heading", { name: "Example Flow" }, waitOptions));
  assert.ok(screen.getByText("v3"));
});

test("running and waiting nodes are highlighted", async (t) => {
  showNodes(t, {
    statusByNodeId: { b: "running" },
    pendingInteractionIdsByNodeId: { a: ["gate"] },
  });
  await findTitle("Writer");
  for (const [title, label] of [
    ["Writer", "Running"],
    ["Input", "Waiting"],
  ] as const) {
    const row = nodeRow(title);
    const statusIcon = within(row).getByLabelText(label);
    assert.equal(statusIcon.parentElement?.getAttribute("aria-current"), "step");
  }
});

test("completed and failed nodes show their icons", async (t) => {
  showNodes(t, { statusByNodeId: { a: "completed", b: "failed" } });
  await findTitle("Writer");
  assert.ok(within(nodeRow("Input")).getByLabelText("Completed"));
  assert.ok(within(nodeRow("Writer")).getByLabelText("Failed"));
});

test("block members are listed and the active member is marked while the block runs", async (t) => {
  const activeMemberIdByNodeId = { block: "1" };
  const update = showNodes(t, { statusByNodeId: { block: "running" }, activeMemberIdByNodeId });
  const reviewer = await screen.findByText("Reviewer member", undefined, waitOptions);
  const members = within(nodeRow("Concurrent Block")).getAllByRole("listitem");
  assert.deepEqual(
    members.map((member) => member.textContent),
    ["Writer member", "Reviewer member"],
  );
  assert.equal(reviewer.getAttribute("aria-current"), "true");
  update({ statusByNodeId: { block: "completed" }, activeMemberIdByNodeId });
  assert.equal(reviewer.getAttribute("aria-current"), null);
});

test("titles fall back to the referenced agent, agentflow, checkpoint name, then kind label", async (t) => {
  showNodes(t);
  for (const title of [
    "Reference Writer",
    "Reference Reviewer",
    "Nested Flow",
    "Draft ready",
    "Input",
  ]) {
    assert.ok(await findTitle(title));
  }
});

test("a long node name stays on one line and keeps the full name in title", async (t) => {
  showNodes(t);
  const title = await findTitle(longName);
  assert.ok(title.classList.contains("truncate"));
  assert.equal(title.textContent, longName);
});

test("the card shows three skeleton rows while the definition loads", async (t) => {
  showNodes(t);
  const card = screen.getByRole("region", { name: "Agentflow nodes" });
  assert.equal(card.querySelectorAll('[data-slot="skeleton"].h-5').length, 3);
  assert.equal(within(card).queryByRole("list"), null);

  await findTitle("Writer");
  assert.equal(card.querySelectorAll('[data-slot="skeleton"]').length, 0);
});

test("a failed definition request shows the error", async (t) => {
  const failing = await startApiServer({});
  configureApiRuntime({ baseUrl: failing.baseUrl, token: null });
  t.after(async () => {
    configureApiRuntime({ baseUrl: api.baseUrl, token: null });
    await failing.close();
  });
  showNodes(t);
  assert.ok(
    await within(screen.getByRole("region", { name: "Agentflow nodes" })).findByText(
      /Not Found/,
      undefined,
      waitOptions,
    ),
  );
});

test("node ids unknown to the definition are ignored", async (t) => {
  showNodes(t, { statusByNodeId: { unknown: "running" } });
  await findTitle("Input");
  assert.equal(screen.queryByLabelText("Running"), null);
  assert.equal(screen.queryByTitle("unknown"), null);
});

test("the first highlighted node scrolls into view when highlights change", async (t) => {
  const scroll = t.mock.method(window.HTMLElement.prototype, "scrollIntoView", () => {});
  const update = showNodes(t);
  await findTitle("Writer");
  assert.equal(scroll.mock.callCount(), 0);
  update({ statusByNodeId: { b: "running" } });
  await waitFor(() => assert.equal(scroll.mock.callCount(), 1));
  const writerRow = within(nodeRow("Writer")).getByLabelText("Running").parentElement;
  assert.ok(scroll.mock.calls[0]?.this === writerRow);
  assert.deepEqual(scroll.mock.calls[0]?.arguments, [{ block: "nearest" }]);
  update({ statusByNodeId: { b: "running" } });
  assert.equal(scroll.mock.callCount(), 1);
  update({ statusByNodeId: { a: "running", b: "running" } });
  await waitFor(() => assert.equal(scroll.mock.callCount(), 2));
  const inputRow = within(nodeRow("Input")).getByLabelText("Running").parentElement;
  assert.ok(scroll.mock.calls[1]?.this === inputRow);
});
