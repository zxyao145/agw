import assert from "node:assert/strict";
import test from "node:test";

import type { Edge } from "reactflow";

import { AgentflowEdgeKind } from "../../../../types/agentflow";
import {
  getDefaultEdgeKindForSource,
  getEdgeRoutingLabel,
  getSwitchCaseOrder,
  moveSwitchCaseEdge,
  normalizeSwitchCaseOrders,
  removeAgentflowEdge,
  setSwitchCaseOrder,
  type AgentflowEdgeData,
  validateAgentflowEdgeRouting,
} from "./agentflow-edge-routing";

function edge(
  id: string,
  source: string,
  target: string,
  kind: AgentflowEdgeKind,
  conditionJson = "",
  configJson = "",
): Edge<AgentflowEdgeData> {
  return {
    id,
    source,
    target,
    data: { kind, label: "", conditionJson, configJson },
  };
}

test("switch order serialization preserves unrelated edge config", () => {
  const configJson = setSwitchCaseOrder('{"mapping":"summary"}', 2);

  assert.deepEqual(JSON.parse(configJson), {
    mapping: "summary",
    switchCaseOrder: 2,
  });
  assert.equal(getSwitchCaseOrder(configJson), 2);
  assert.deepEqual(JSON.parse(setSwitchCaseOrder(configJson, null)), {
    mapping: "summary",
  });
  assert.equal(setSwitchCaseOrder("{", null), "{");
});

test("normalizing and moving switch cases maintains contiguous order", () => {
  const edges = [
    edge(
      "case-b",
      "input",
      "b",
      AgentflowEdgeKind.SwitchCase,
      '{"contains":"b"}',
      '{"switchCaseOrder":7,"mapping":"b"}',
    ),
    edge("case-a", "input", "a", AgentflowEdgeKind.SwitchCase, '{"contains":"a"}'),
    edge("else", "input", "fallback", AgentflowEdgeKind.SwitchDefault),
    edge(
      "other-case",
      "other-source",
      "other-target",
      AgentflowEdgeKind.SwitchCase,
      '{"contains":"other"}',
      '{"switchCaseOrder":4}',
    ),
  ];

  const normalized = normalizeSwitchCaseOrders(edges, "input");
  assert.equal(getSwitchCaseOrder(normalized[0].data?.configJson ?? ""), 0);
  assert.equal(getSwitchCaseOrder(normalized[1].data?.configJson ?? ""), 1);

  const moved = moveSwitchCaseEdge(normalized, "case-a", -1);
  assert.equal(getSwitchCaseOrder(moved[0].data?.configJson ?? ""), 1);
  assert.equal(getSwitchCaseOrder(moved[1].data?.configJson ?? ""), 0);
  assert.equal(JSON.parse(moved[0].data?.configJson ?? "{}").mapping, "b");
  assert.equal(moved[1].data?.conditionJson, '{"contains":"a"}');
  assert.equal(getSwitchCaseOrder(moved[3].data?.configJson ?? ""), 4);
});

test("deleting a switch case reorders remaining cases", () => {
  const edges = [
    edge(
      "case-a",
      "input",
      "a",
      AgentflowEdgeKind.SwitchCase,
      '{"contains":"a"}',
      '{"switchCaseOrder":0}',
    ),
    edge(
      "case-b",
      "input",
      "b",
      AgentflowEdgeKind.SwitchCase,
      '{"contains":"b"}',
      '{"switchCaseOrder":1}',
    ),
  ];

  const remaining = removeAgentflowEdge(edges, "case-a");

  assert.deepEqual(
    remaining.map((item) => item.id),
    ["case-b"],
  );
  assert.equal(getSwitchCaseOrder(remaining[0].data?.configJson ?? ""), 0);
});

test("switch labels reflect ordered If, Else If, and Else branches", () => {
  const edges = normalizeSwitchCaseOrders([
    edge("case-a", "input", "a", AgentflowEdgeKind.SwitchCase, '{"contains":"a"}'),
    edge("case-b", "input", "b", AgentflowEdgeKind.SwitchCase, '{"contains":"b"}'),
    edge("else", "input", "fallback", AgentflowEdgeKind.SwitchDefault),
  ]);

  assert.equal(getEdgeRoutingLabel(edges[0], edges), "IF");
  assert.equal(getEdgeRoutingLabel(edges[1], edges), "ELSE IF");
  assert.equal(getEdgeRoutingLabel(edges[2], edges), "ELSE");
});

test("routing validation rejects mixed strategies and duplicate Else", () => {
  assert.match(
    validateAgentflowEdgeRouting([
      edge("direct", "input", "a", AgentflowEdgeKind.Direct),
      edge("fan-out", "input", "b", AgentflowEdgeKind.FanOut),
    ]) ?? "",
    /cannot mix/,
  );

  const duplicateElse = normalizeSwitchCaseOrders([
    edge("case", "input", "a", AgentflowEdgeKind.SwitchCase, '{"contains":"a"}'),
    edge("else-a", "input", "b", AgentflowEdgeKind.SwitchDefault),
    edge("else-b", "input", "c", AgentflowEdgeKind.SwitchDefault),
  ]);
  assert.match(validateAgentflowEdgeRouting(duplicateElse) ?? "", /only have one Else/);
});

test("barrier edges coexist with ordinary routing and Input supports every strategy", () => {
  const directAndBarrier = [
    edge("direct", "input", "a", AgentflowEdgeKind.Direct),
    edge("barrier", "input", "b", AgentflowEdgeKind.FanInBarrier),
  ];

  assert.equal(validateAgentflowEdgeRouting(directAndBarrier), null);
  assert.equal(
    getDefaultEdgeKindForSource([], "input", AgentflowEdgeKind.FanOut),
    AgentflowEdgeKind.FanOut,
  );
  assert.equal(
    getDefaultEdgeKindForSource(directAndBarrier, "input", AgentflowEdgeKind.FanOut),
    AgentflowEdgeKind.Direct,
  );
  assert.equal(
    getDefaultEdgeKindForSource(
      [edge("barrier", "input", "b", AgentflowEdgeKind.FanInBarrier)],
      "input",
      AgentflowEdgeKind.FanOut,
    ),
    AgentflowEdgeKind.FanOut,
  );
});

test("conditional and unconditional Fan Out edges are both valid multi-selection routes", () => {
  const edges = [
    edge("always", "input", "a", AgentflowEdgeKind.FanOut),
    edge("when", "input", "b", AgentflowEdgeKind.FanOut, '{"contains":"b"}'),
  ];

  assert.equal(validateAgentflowEdgeRouting(edges), null);
  assert.equal(getEdgeRoutingLabel(edges[0], edges), "FAN OUT");
  assert.equal(getEdgeRoutingLabel(edges[1], edges), "WHEN");
});
