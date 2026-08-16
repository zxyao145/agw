import assert from "node:assert/strict";
import test from "node:test";

import type { Edge, Node } from "reactflow";

async function loadInputNodeRules() {
  return await import("./agentflow-input-node" + ".ts");
}

const AgentflowNodeKind = {
  Agent: 0,
  PromptAdapter: 2,
  Output: 9,
  Input: 10,
} as const;

type AgentflowNodeKind = (typeof AgentflowNodeKind)[keyof typeof AgentflowNodeKind];

const AgentflowEdgeKind = {
  Direct: 0,
  FanOut: 1,
} as const;

type AgentflowEdgeKind = (typeof AgentflowEdgeKind)[keyof typeof AgentflowEdgeKind];

type TestNodeData = {
  kind: AgentflowNodeKind;
  title: string;
  relateId: string | null;
  instructions: string;
  configJson: string;
};

type TestEdgeData = {
  kind: AgentflowEdgeKind;
  label: string;
  conditionJson: string;
  configJson: string;
};

function node(
  id: string,
  kind: AgentflowNodeKind,
  config: Record<string, unknown> = {},
): Node<TestNodeData> {
  return {
    id,
    type: "dagNode",
    position: { x: 0, y: 0 },
    data: {
      kind,
      title: id,
      relateId: null,
      instructions: "",
      configJson: Object.keys(config).length > 0 ? JSON.stringify(config) : "",
    },
  };
}

function edge(
  id: string,
  source: string,
  target: string,
  kind: AgentflowEdgeKind = AgentflowEdgeKind.Direct,
): Edge<TestEdgeData> {
  return {
    id,
    source,
    target,
    data: {
      kind,
      label: "",
      conditionJson: "",
      configJson: "",
    },
  };
}

test("ensureInputGraph inserts input for a new graph", async () => {
  const { ensureInputGraph, INPUT_NODE_ID } = await loadInputNodeRules();

  const result = ensureInputGraph([], []);

  assert.equal(result.nodes.length, 1);
  assert.equal(result.nodes[0].id, INPUT_NODE_ID);
  assert.equal(result.nodes[0].data.kind, AgentflowNodeKind.Input);
  assert.deepEqual(result.edges, []);
});

test("ensureInputGraph connects legacy roots from input with FanOut edges", async () => {
  const { ensureInputGraph, INPUT_NODE_ID } = await loadInputNodeRules();
  const nodes = [
    node("adapter", AgentflowNodeKind.PromptAdapter),
    node("agent", AgentflowNodeKind.Agent),
    node("output", AgentflowNodeKind.Output),
  ];
  const edges = [edge("agent-output", "agent", "output")];

  const result = ensureInputGraph(nodes, edges);
  const inputEdges = result.edges.filter((item) => item.source === INPUT_NODE_ID);

  assert.deepEqual(inputEdges.map((item) => [item.target, item.data?.kind]).sort(), [
    ["adapter", AgentflowEdgeKind.FanOut],
    ["agent", AgentflowEdgeKind.FanOut],
  ]);
});

test("validateInputGraph rejects incoming edges to input", async () => {
  const { validateInputGraph, INPUT_NODE_ID } = await loadInputNodeRules();
  const nodes = [
    node(INPUT_NODE_ID, AgentflowNodeKind.Input),
    node("agent", AgentflowNodeKind.Agent),
  ];

  const result = validateInputGraph(nodes, [
    edge("bad", "agent", INPUT_NODE_ID, AgentflowEdgeKind.Direct),
  ]);

  assert.equal(result.ok, false);
  assert.match(result.message, /Input cannot have incoming edges/);
});

test("validateInputGraph allows non-FanOut routing from input", async () => {
  const { validateInputGraph, INPUT_NODE_ID } = await loadInputNodeRules();
  const nodes = [
    node(INPUT_NODE_ID, AgentflowNodeKind.Input),
    node("agent", AgentflowNodeKind.Agent),
  ];

  const result = validateInputGraph(nodes, [
    edge("bad", INPUT_NODE_ID, "agent", AgentflowEdgeKind.Direct),
  ]);

  assert.equal(result.ok, true);
});

test("validateInputGraph rejects visible nodes unreachable from input", async () => {
  const { validateInputGraph, INPUT_NODE_ID } = await loadInputNodeRules();
  const nodes = [
    node(INPUT_NODE_ID, AgentflowNodeKind.Input),
    node("agent", AgentflowNodeKind.Agent),
    node("output", AgentflowNodeKind.Output),
  ];

  const result = validateInputGraph(nodes, [
    edge("agent-output", "agent", "output", AgentflowEdgeKind.Direct),
  ]);

  assert.equal(result.ok, false);
  assert.match(result.message, /must start from Input/);
});
