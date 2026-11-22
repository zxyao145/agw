import assert from "node:assert/strict";
import test from "node:test";

import {
  createEdge,
  createEmptyDocument,
  createNode,
  type AgentflowEditorDocument,
} from "./agentflow-document";
import {
  clearSwitchDefault,
  removeGateChoice,
  reorderSwitchCase,
  renumberSwitchCases,
  setFanInBarrier,
  setRoutingStrategy,
  setSwitchDefault,
} from "./agentflow-edge-routing";

function createSwitchDocument(): AgentflowEditorDocument {
  const input = createEmptyDocument().nodes[0];
  const source = createNode("agent", { x: 200, y: 0 });
  source.routingStrategy = "switch";
  const first = createNode("output", { x: 500, y: 0 });
  const second = createNode("output", { x: 500, y: 100 });
  const fallback = createNode("output", { x: 500, y: 200 });
  const document = {
    ...createEmptyDocument(),
    nodes: [input, source, first, second, fallback],
    edges: [],
  };
  const firstEdge = createEdge(document, source.nodeId, first.nodeId);
  const secondEdge = createEdge({ ...document, edges: [firstEdge] }, source.nodeId, second.nodeId);
  const elseEdge = {
    ...createEdge({ ...document, edges: [firstEdge, secondEdge] }, source.nodeId, fallback.nodeId),
    isSwitchDefault: true,
  };

  return {
    ...document,
    edges: [
      { ...firstEdge, conditions: [{ field: "contains", value: "one", path: "" }] },
      { ...secondEdge, conditions: [{ field: "contains", value: "two", path: "" }] },
      elseEdge,
    ],
  };
}

test("switching to Branch numbers the branches in list order and keeps conditions", () => {
  const document = createSwitchDocument();
  const source = document.nodes[1];
  source.routingStrategy = "direct";
  const next = setRoutingStrategy(document, source.nodeId, "switch");

  assert.deepEqual(
    next.edges.slice(0, 2).map((edge) => edge.switchCaseOrder),
    [0, 1],
  );
  assert.deepEqual(next.edges[0].conditions, document.edges[0].conditions);
});

test("leaving Branch clears Else and order", () => {
  const document = createSwitchDocument();
  const next = setRoutingStrategy(document, document.nodes[1].nodeId, "direct");

  assert.equal(
    next.edges.some((edge) => edge.isSwitchDefault),
    false,
  );
  assert.deepEqual(
    next.edges.map((edge) => edge.switchCaseOrder),
    [0, 0, 0],
  );
});

test("making a branch the Else clears its conditions", () => {
  const document = createSwitchDocument();
  const next = setSwitchDefault(document, document.edges[0].edgeId);
  const nextDefault = next.edges.find((edge) => edge.edgeId === document.edges[0].edgeId)!;
  const previousDefault = next.edges.find((edge) => edge.edgeId === document.edges[2].edgeId)!;

  assert.equal(nextDefault.isSwitchDefault, true);
  assert.deepEqual(nextDefault.conditions, []);
  assert.equal(previousDefault.isSwitchDefault, false);
  assert.equal(previousDefault.switchCaseOrder, 1);
});

test("turning Wait on clears conditions, choice and branch fields", () => {
  const document = createSwitchDocument();
  const next = setFanInBarrier(document, document.edges[0].edgeId, true);
  const waiting = next.edges[0];

  assert.equal(waiting.isFanInBarrier, true);
  assert.deepEqual(waiting.conditions, []);
  assert.equal(waiting.choiceId, null);
  assert.equal(waiting.isSwitchDefault, false);
  assert.equal(waiting.switchCaseOrder, 0);
});

test("turning Wait off appends a branch before Else", () => {
  const document = createSwitchDocument();
  const [first, second, elseEdge] = document.edges;
  const waiting = setFanInBarrier(document, first.edgeId, true);
  const restored = setFanInBarrier(waiting, first.edgeId, false);

  assert.equal(restored.edges[0].isFanInBarrier, false);
  assert.equal(restored.edges[0].switchCaseOrder, second.switchCaseOrder + 1);
  assert.deepEqual(restored.edges[1], second);
  assert.deepEqual(restored.edges[2], elseEdge);
});

test("turning Wait off assigns a Human Gate edge to an available choice", () => {
  const document = createEmptyDocument();
  const gate = createNode("humanGate", { x: 200, y: 0 });
  const first = createNode("output", { x: 500, y: 0 });
  const second = createNode("output", { x: 500, y: 100 });
  if (gate.kind !== "humanGate") assert.fail("Expected a Human Gate");
  gate.choices.push({ choiceId: "second-choice", label: "Continue" });
  const graph = { ...document, nodes: [...document.nodes, gate, first, second] };
  const firstEdge = createEdge(graph, gate.nodeId, first.nodeId);
  const secondEdge = {
    ...createEdge(graph, gate.nodeId, second.nodeId),
    isFanInBarrier: true,
    choiceId: null,
    label: "Review",
  };
  const restored = setFanInBarrier(
    { ...graph, edges: [firstEdge, secondEdge] },
    secondEdge.edgeId,
    false,
  );

  assert.equal(restored.edges[1].choiceId, "second-choice");
  assert.equal(restored.edges[1].label, "Review");
});

test("removing a choice removes its edges", () => {
  const document = createEmptyDocument();
  const gate = createNode("humanGate", { x: 200, y: 0 });
  const target = createNode("output", { x: 500, y: 0 });
  if (gate.kind !== "humanGate") assert.fail("Expected a Human Gate");
  gate.choices.push({ choiceId: "choice-2", label: "Continue" });
  const graph = { ...document, nodes: [...document.nodes, gate, target] };
  const edge = createEdge(graph, gate.nodeId, target.nodeId);
  const next = removeGateChoice({ ...graph, edges: [edge] }, gate.nodeId, edge.choiceId!);

  assert.deepEqual(next.edges, []);
  assert.equal(next.nodes.find((node) => node.nodeId === gate.nodeId)?.kind, "humanGate");
  const remaining = next.nodes.find((node) => node.nodeId === gate.nodeId);
  assert.equal(
    remaining?.kind === "humanGate" &&
      remaining.choices.some((choice) => choice.choiceId === edge.choiceId),
    false,
  );
});

test("moving a branch renumbers the branches", () => {
  const document = createSwitchDocument();
  const next = reorderSwitchCase(document, document.edges[1].edgeId, 0);

  const ordered = next.edges
    .filter((edge) => !edge.isSwitchDefault)
    .sort((left, right) => left.switchCaseOrder - right.switchCaseOrder);
  assert.deepEqual(
    ordered.map((edge) => edge.edgeId),
    [document.edges[1].edgeId, document.edges[0].edgeId],
  );
  assert.deepEqual(
    ordered.map((edge) => edge.switchCaseOrder),
    [0, 1],
  );
});

test("the previous Else becomes the last conditional branch", () => {
  const document = createSwitchDocument();
  const next = clearSwitchDefault(document, document.edges[2].edgeId);
  const previousElse = next.edges.find((edge) => edge.edgeId === document.edges[2].edgeId)!;

  assert.equal(previousElse.isSwitchDefault, false);
  assert.equal(previousElse.switchCaseOrder, 2);
});

test("deleting a branch renumbers the rest", () => {
  const document = createSwitchDocument();
  const remaining = {
    ...document,
    edges: document.edges.filter((edge) => edge.edgeId !== document.edges[0].edgeId),
  };
  const next = renumberSwitchCases(remaining, [document.nodes[1].nodeId]);

  assert.deepEqual(
    next.edges.filter((edge) => !edge.isSwitchDefault).map((edge) => edge.switchCaseOrder),
    [0],
  );
});
