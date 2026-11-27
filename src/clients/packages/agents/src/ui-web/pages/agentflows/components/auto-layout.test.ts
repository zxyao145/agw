import assert from "node:assert/strict";
import test from "node:test";

import { AGENTFLOW_NODE_SIZE } from "./agentflow-canvas-geometry";
import {
  applyNodePositions,
  createEdge,
  createEmptyDocument,
  createNode,
} from "./agentflow-document";
import { routeAgentflowEdges } from "./agentflow-edge-path";
import { createGraphLayout } from "./auto-layout";

test("layout uses the 20px grid and reserves space for edge labels", async () => {
  const document = createEmptyDocument();
  const output = createNode("output", { x: 0, y: 0 });
  const labelText = "an expected result long enough to require more space around the label";
  document.nodes.push(output);
  document.edges.push({
    ...createEdge(document, "input", output.nodeId),
    conditions: [{ field: "contains", value: labelText, path: "" }],
  });
  const positions = await createGraphLayout(
    document,
    { input: { width: 220, height: 80 }, [output.nodeId]: { width: 220, height: 80 } },
    { width: 1800, height: 120 },
  );
  for (const { x, y } of Object.values(positions)) {
    assert.equal(x % 20, 0);
    assert.equal(y % 20, 0);
  }
  const betweenLayers = positions[output.nodeId].x - positions.input.x - 220;
  assert.ok(betweenLayers >= labelText.length * 6.2 + 18, JSON.stringify(positions));
});

test("the longest flow stays horizontal at a fork with a bypass", async () => {
  const document = createEmptyDocument();
  const origin = { x: 0, y: 0 };
  const ready = createNode("humanGate", origin);
  assert.equal(ready.kind, "humanGate");
  ready.choices = [
    { choiceId: "yes", label: "Yes" },
    { choiceId: "no", label: "No" },
  ];
  document.nodes.push(
    { ...createNode("groupChatBlock", origin), nodeId: "group-chat" },
    { ...createNode("clearMessages", origin), nodeId: "clear-messages", routingStrategy: "fanOut" },
    { ...createNode("promptAdapter", origin), nodeId: "prompt-adapter" },
    { ...ready, nodeId: "ready" },
    { ...createNode("agent", origin), nodeId: "commit" },
    { ...createNode("output", origin), nodeId: "output" },
  );
  const connections = [
    ["input", "group-chat"],
    ["group-chat", "clear-messages"],
    ["clear-messages", "prompt-adapter"],
    ["clear-messages", "ready"],
    ["ready", "commit", "yes"],
    ["ready", "output", "no"],
    ["commit", "output"],
  ];
  document.edges = connections.map(([source, target, choiceId]) => ({
    ...createEdge(document, source, target),
    choiceId: choiceId ?? null,
  }));

  const groupChatSize = { width: 240, height: 158 };
  const positions = await createGraphLayout(
    document,
    { "group-chat": groupChatSize },
    { width: 800, height: 500 },
  );

  for (const [source, target] of connections) {
    assert.ok(positions[source].x < positions[target].x, `${source} should be left of ${target}`);
  }
  assert.equal(positions.ready.y, positions["clear-messages"].y);
  const mainPath = ["input", "group-chat", "clear-messages", "ready", "commit", "output"];
  const mainY = positions.input.y + AGENTFLOW_NODE_SIZE.height / 2;

  const arranged = applyNodePositions(document, positions);
  const routes = routeAgentflowEdges(
    arranged.nodes.map(({ nodeId, position }) => ({
      id: nodeId,
      x: Number(position.x),
      y: Number(position.y),
      ...(nodeId === "group-chat" ? groupChatSize : AGENTFLOW_NODE_SIZE),
    })),
    arranged.edges,
  );
  for (let index = 1; index < mainPath.length; index += 1) {
    const source = mainPath[index - 1];
    const target = mainPath[index];
    const edge = document.edges.find(
      (candidate) => candidate.sourceNodeId === source && candidate.targetNodeId === target,
    );
    assert.ok(edge);
    assert.deepEqual(
      routes[edge.edgeId].points.map(({ y }) => y),
      [mainY, mainY],
      `${source} to ${target}`,
    );
  }
  const branchPosition = positions["prompt-adapter"];
  assert.equal(branchPosition.x, positions.ready.x);
  assert.ok(Math.abs(branchPosition.y - positions.ready.y) - AGENTFLOW_NODE_SIZE.height >= 60);

  const bypass = document.edges.find((edge) => edge.choiceId === "no");
  assert.ok(bypass);
  assert.ok(routes[bypass.edgeId].points.some(({ y }) => y < positions.commit.y));
  assert.ok(routes[bypass.edgeId].label.y < positions.commit.y);
});

test("parallel nodes in the same column have 60 pixels of vertical spacing", async () => {
  const document = createEmptyDocument();
  document.nodes[0].routingStrategy = "fanOut";
  const origin = { x: 0, y: 0 };
  const firstBranch = createNode("agent", origin);
  const secondBranch = createNode("agent", origin);
  const output = createNode("output", origin);
  document.nodes.push(firstBranch, secondBranch, output);
  document.edges.push(
    createEdge(document, "input", firstBranch.nodeId),
    createEdge(document, "input", secondBranch.nodeId),
    createEdge(document, firstBranch.nodeId, output.nodeId),
    createEdge(document, secondBranch.nodeId, output.nodeId),
  );
  const size = { width: 240, height: 60 };
  const sizes = Object.fromEntries(document.nodes.map(({ nodeId }) => [nodeId, size]));
  const positions = await createGraphLayout(document, sizes, { width: 800, height: 500 });
  const first = positions[firstBranch.nodeId];
  const second = positions[secondBranch.nodeId];

  assert.equal(first.x, second.x);
  assert.equal(Math.abs(first.y - second.y) - size.height, 60);
});
