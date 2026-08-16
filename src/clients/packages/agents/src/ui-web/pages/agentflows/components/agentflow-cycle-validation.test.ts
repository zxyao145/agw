import assert from "node:assert/strict";
import test from "node:test";

import { AgentflowEdgeKind, AgentflowNodeKind } from "../../../../types/agentflow";
import { validateAgentflowCycles } from "./agentflow-cycle-validation";

function node(id: string, kind = AgentflowNodeKind.PromptAdapter) {
  return { id, data: { kind, title: id } };
}

function edge(source: string, target: string, kind = AgentflowEdgeKind.Direct) {
  return { source, target, data: { kind } };
}

test("accepts a cycle with a Switch exit and reusable Input barrier", () => {
  const nodes = [
    node("input", AgentflowNodeKind.Input),
    node("upper"),
    node("lower"),
    node("human", AgentflowNodeKind.HumanGate),
    node("output", AgentflowNodeKind.Output),
  ];
  const edges = [
    edge("input", "upper", AgentflowEdgeKind.FanOut),
    edge("input", "lower", AgentflowEdgeKind.FanInBarrier),
    edge("upper", "lower", AgentflowEdgeKind.FanInBarrier),
    edge("lower", "human"),
    edge("human", "upper", AgentflowEdgeKind.SwitchCase),
    edge("human", "output", AgentflowEdgeKind.SwitchDefault),
  ];

  assert.equal(validateAgentflowCycles(nodes, edges), null);
});

test("rejects a cycle without a Switch exit", () => {
  const nodes = [node("input", AgentflowNodeKind.Input), node("a"), node("b")];
  const edges = [edge("input", "a"), edge("a", "b"), edge("b", "a")];

  assert.match(
    validateAgentflowCycles(nodes, edges) ?? "",
    /needs an If \/ Else branch that exits the cycle/,
  );
});

test("rejects a cyclic barrier fed by an external non-Input node", () => {
  const nodes = [
    node("input", AgentflowNodeKind.Input),
    node("seed"),
    node("a"),
    node("b"),
    node("output", AgentflowNodeKind.Output),
  ];
  const edges = [
    edge("input", "seed", AgentflowEdgeKind.FanOut),
    edge("input", "a", AgentflowEdgeKind.FanOut),
    edge("seed", "b", AgentflowEdgeKind.FanInBarrier),
    edge("a", "b", AgentflowEdgeKind.FanInBarrier),
    edge("b", "a", AgentflowEdgeKind.SwitchCase),
    edge("b", "output", AgentflowEdgeKind.SwitchDefault),
  ];

  assert.equal(
    validateAgentflowCycles(nodes, edges),
    "A Fan-in Barrier entering a cycle can only reuse the Input node from outside the cycle",
  );
});
