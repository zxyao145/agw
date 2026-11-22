import assert from "node:assert/strict";
import test from "node:test";

import type {
  AgentflowConditionRule,
  AgentflowEdge,
  AgentflowNode,
} from "../../../../types/agentflow";
import {
  addConditionRule,
  describeAgentflowEdge,
  describeConditions,
  getAvailableConditionFields,
  removeConditionRule,
  updateConditionRule,
} from "./agentflow-condition";

const edge = (values: Partial<AgentflowEdge> = {}): AgentflowEdge => ({
  edgeId: "edge-1",
  sourceNodeId: "agent",
  targetNodeId: "output",
  isFanInBarrier: false,
  isSwitchDefault: false,
  switchCaseOrder: 0,
  choiceId: null,
  label: null,
  conditions: [],
  ...values,
});

const node = (values: Partial<AgentflowNode> = {}): AgentflowNode =>
  ({
    kind: "agent",
    nodeId: "agent",
    name: "Agent",
    position: { x: 0, y: 0 },
    routingStrategy: "direct",
    ...values,
  }) as AgentflowNode;

test("descriptions for every field", () => {
  const rules: AgentflowConditionRule[] = [
    { field: "contains", value: "yes", path: "" },
    { field: "notContains", value: "no", path: "" },
    { field: "equals", value: "yes", path: "" },
    { field: "author", value: "Ada", path: "" },
    { field: "role", value: "assistant", path: "" },
    { field: "minMessages", value: "2", path: "" },
    { field: "result", value: "approved", path: "review.status" },
  ];

  assert.equal(
    describeConditions(rules),
    [
      'contains "yes"',
      'does not contain "no"',
      'equals "yes"',
      "author is Ada",
      "role is assistant",
      "at least 2 messages",
      'review.status is "approved"',
    ].join(" and "),
  );
});

test("rows join with and", () => {
  assert.equal(
    describeConditions([
      { field: "contains", value: "ready", path: "" },
      { field: "author", value: "Ada", path: "" },
    ]),
    'contains "ready" and author is Ada',
  );
});

test("result rules show the path", () => {
  assert.equal(
    describeConditions([{ field: "result", value: "ok", path: "review.status" }]),
    'review.status is "ok"',
  );
});

test("edge labels follow the source strategy", () => {
  const input = node({ nodeId: "input", kind: "input", routingStrategy: "fanOut" });
  const agent = node({ routingStrategy: "switch" });
  const gate = node({
    nodeId: "gate",
    kind: "humanGate",
    routingStrategy: "choice",
    choices: [{ choiceId: "approve", label: "Approve" }],
  });
  const targets = [input, agent, gate];

  assert.equal(describeAgentflowEdge(edge({ isFanInBarrier: true }), targets, []), "wait");
  assert.equal(
    describeAgentflowEdge(
      edge({ conditions: [{ field: "contains", value: "ok", path: "" }] }),
      targets,
      [],
    ),
    'if contains "ok"',
  );
  assert.equal(
    describeAgentflowEdge(
      edge({
        edgeId: "second",
        conditions: [{ field: "contains", value: "ok", path: "" }],
        switchCaseOrder: 1,
      }),
      targets,
      [
        edge({ conditions: [{ field: "contains", value: "yes", path: "" }] }),
        edge({ edgeId: "second", switchCaseOrder: 1 }),
      ],
    ),
    'else if contains "ok"',
  );
  assert.equal(describeAgentflowEdge(edge({ isSwitchDefault: true }), targets, []), "else");
  assert.equal(
    describeAgentflowEdge(edge({ sourceNodeId: "gate", choiceId: "approve" }), targets, []),
    "Approve",
  );
  assert.equal(
    describeAgentflowEdge(
      edge({ sourceNodeId: "input", conditions: [{ field: "equals", value: "ok", path: "" }] }),
      [input],
      [],
    ),
    'when equals "ok"',
  );
  assert.equal(describeAgentflowEdge(edge(), [input], []), "");
});

test("result is offered only when the source produces a structured Result", () => {
  const fields = ["contains", "notContains", "equals", "author", "role", "minMessages"];
  assert.deepEqual(getAvailableConditionFields([], false), fields);
  assert.deepEqual(getAvailableConditionFields([], true), [...fields, "result"]);
});

test("fields other than result appear once", () => {
  const rules: AgentflowConditionRule[] = [
    { field: "contains", value: "a", path: "" },
    { field: "result", value: "b", path: "status" },
  ];

  assert.deepEqual(getAvailableConditionFields(rules, true), [
    "notContains",
    "equals",
    "author",
    "role",
    "minMessages",
    "result",
  ]);
  assert.deepEqual(addConditionRule(rules, "result"), [
    ...rules,
    { field: "result", value: "", path: "" },
  ]);
  assert.deepEqual(updateConditionRule(rules, 0, { value: "changed" })[0], {
    ...rules[0],
    value: "changed",
  });
  assert.deepEqual(removeConditionRule(rules, 1), [rules[0]]);
});
