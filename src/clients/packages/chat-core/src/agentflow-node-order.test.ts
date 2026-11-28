import assert from "node:assert/strict";
import test from "node:test";
import type { components } from "@agw/api";
import { orderAgentflowNodes } from "./agentflow-node-order";

type Node = components["schemas"]["AgentflowNodeDto"];
type Edge = components["schemas"]["AgentflowEdgeDto"];

function node(nodeId: string, y: number | string = 0, x: number | string = 0): Node {
  return {
    nodeId,
    name: null,
    kind: "clearMessages",
    position: { x, y },
    routingStrategy: "direct",
  };
}

function input(nodeId: string, y = 0): Node {
  return { ...node(nodeId, y), kind: "input" };
}

function order(nodes: Node[], connections: [string, string][] = []): string[] {
  const edges = connections.map(([sourceNodeId, targetNodeId], index): Edge => ({
    edgeId: `edge-${index}`,
    sourceNodeId,
    targetNodeId,
    isFanInBarrier: false,
    isSwitchDefault: false,
    switchCaseOrder: 0,
    choiceId: null,
    label: null,
    conditions: [],
  }));
  return orderAgentflowNodes({ nodes, edges }).map(({ nodeId }) => nodeId);
}

test("orders the design example by layer", () => {
  const nodes = [
    node("tester", 200),
    node("output"),
    node("gate"),
    node("coder"),
    node("reviewer", 0),
    input("input"),
    node("planner"),
  ];
  assert.deepEqual(
    order(nodes, [
      ["input", "planner"],
      ["planner", "gate"],
      ["gate", "coder"],
      ["gate", "planner"],
      ["coder", "tester"],
      ["coder", "reviewer"],
      ["reviewer", "output"],
      ["tester", "output"],
    ]),
    ["input", "planner", "gate", "coder", "reviewer", "tester", "output"],
  );
});

test("places a fan-in node after all of its predecessors", () => {
  const nodes = [input("input"), node("a"), node("b", 200), node("c"), node("d")];
  assert.deepEqual(
    order(nodes, [
      ["input", "a"],
      ["a", "b"],
      ["b", "d"],
      ["input", "c"],
      ["c", "d"],
    ]),
    ["input", "a", "c", "b", "d"],
  );
});

test("sorts one layer by y, then x, then definition order", () => {
  const nodes = [
    input("input"),
    node("d", 10, 0),
    node("a", 0, 10),
    node("b", 0, 0),
    node("c", 0, 0),
  ];
  assert.deepEqual(
    order(nodes, [
      ["input", "a"],
      ["input", "d"],
      ["input", "c"],
      ["input", "b"],
    ]),
    ["input", "b", "c", "a", "d"],
  );
});

test("starts from every input node", () => {
  const nodes = [node("b"), input("left"), node("a"), input("right", 1), node("c")];
  assert.deepEqual(
    order(nodes, [
      ["left", "c"],
      ["right", "a"],
      ["a", "b"],
      ["b", "a"],
    ]),
    ["left", "right", "a", "c", "b"],
  );
});

test("places nodes without edges by position", () => {
  assert.deepEqual(order([node("b", 10), node("c", 0, 20), node("a", 0, 10)]), ["a", "c", "b"]);
});

test("compares string positions as numbers", () => {
  assert.deepEqual(order([node("b", "120"), node("a", "80")]), ["a", "b"]);
});
