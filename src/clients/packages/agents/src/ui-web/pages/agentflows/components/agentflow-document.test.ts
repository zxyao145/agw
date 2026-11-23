import assert from "node:assert/strict";
import test from "node:test";

import {
  addMember,
  createEdge,
  createEditorDocument,
  createEmptyDocument,
  createNode,
  insertNodeOnEdge,
  isValidConnection,
  nodeOverlapsAnother,
  removeMember,
  replaceEditorGraph,
  retargetEdge,
  searchNodes,
  type AgentflowEditorDocument,
} from "./agentflow-document";

test("an empty document has only the Input node", () => {
  const document = createEmptyDocument();

  assert.equal(document.name, "");
  assert.equal(document.description, "");
  assert.equal(document.nodes.length, 1);
  assert.equal(document.nodes[0].kind, "input");
  assert.deepEqual(document.edges, []);
});

test("createEditorDocument keeps node order, positions and members", () => {
  const document = createEditorDocument(
    { name: "Flow", description: "Description" },
    {
      nodes: [
        {
          kind: "input",
          nodeId: "input",
          name: "Input",
          position: { x: 20, y: 40 },
          routingStrategy: "direct",
        },
        {
          kind: "magenticBlock",
          nodeId: "team",
          name: "Team",
          position: { x: 300, y: 160 },
          routingStrategy: "direct",
          instructions: "Work together",
          members: [
            {
              memberId: "manager",
              kind: "agent",
              relateId: null,
              name: "Manager",
              instructions: null,
              isManager: true,
            },
          ],
          maxRounds: 3,
          maxStalls: 2,
          maxResets: 1,
          requirePlanSignoff: false,
        },
      ],
      edges: [],
    },
  );

  assert.equal(document.name, "Flow");
  assert.deepEqual(
    document.nodes.map((node) => node.nodeId),
    ["input", "team"],
  );
  assert.deepEqual(document.nodes[1].position, { x: 300, y: 160 });
  const team = document.nodes[1];
  assert.equal(team.kind === "magenticBlock" ? team.members[0]?.memberId : undefined, "manager");
});

test("replacing a revision graph keeps the current name and description", () => {
  const current = {
    ...createEmptyDocument(),
    name: "Current name",
    description: "Current description",
  };
  const historicalGraph = {
    nodes: [createNode("agent", { x: 200, y: 40 })],
    edges: [],
  };

  const next = replaceEditorGraph(current, historicalGraph);

  assert.equal(next.name, current.name);
  assert.equal(next.description, current.description);
  assert.deepEqual(next.nodes, historicalGraph.nodes);
  assert.deepEqual(next.edges, historicalGraph.edges);
});

test("node overlap checks use loaded numeric string positions and measured sizes", () => {
  const document: AgentflowEditorDocument = {
    ...createEmptyDocument(),
    nodes: [
      { ...createNode("agent", { x: 0, y: 0 }), nodeId: "existing" },
      {
        ...createNode("agent", { x: 300, y: 80 }),
        nodeId: "candidate",
        position: { x: "200", y: "80" },
      },
    ],
  };
  const sizes = {
    existing: { width: 240, height: 120 },
    candidate: { width: 240, height: 64 },
  };

  assert.equal(nodeOverlapsAnother(document, "candidate", sizes), true);
  assert.equal(nodeOverlapsAnother(document, "missing", sizes), false);
});

test("a new Human Gate has the choice strategy and one choice named Continue", () => {
  const gate = createNode("humanGate", { x: 20, y: 40 });

  assert.equal(gate.kind, "humanGate");
  if (gate.kind !== "humanGate") assert.fail("Expected a Human Gate");
  assert.equal(gate.routingStrategy, "choice");
  assert.deepEqual(
    gate.choices.map((choice) => choice.label),
    ["Continue"],
  );
});

test("the first member of a Magentic Team is the manager", () => {
  const team = createNode("magenticBlock", { x: 0, y: 0 });
  const withMember = addMember({ ...createEmptyDocument(), nodes: [team] }, team.nodeId, {
    kind: "agent",
    relateId: "agent-1",
    name: "Agent",
  });

  assert.equal(withMember.nodes[0].kind, "magenticBlock");
  if (withMember.nodes[0].kind !== "magenticBlock") assert.fail("Expected a Magentic Team");
  assert.equal(withMember.nodes[0].members[0].isManager, true);
});

test("removing the manager makes the first member the manager", () => {
  const team = createNode("magenticBlock", { x: 0, y: 0 });
  let document = addMember({ ...createEmptyDocument(), nodes: [team] }, team.nodeId, {
    kind: "agent",
    relateId: "agent-1",
    name: "First",
  });
  document = addMember(document, team.nodeId, {
    kind: "agent",
    relateId: "agent-2",
    name: "Second",
  });
  const manager =
    document.nodes[0].kind === "magenticBlock" ? document.nodes[0].members[0] : undefined;
  const remaining = removeMember(document, team.nodeId, manager!.memberId);

  assert.equal(remaining.nodes[0].kind, "magenticBlock");
  if (remaining.nodes[0].kind !== "magenticBlock") assert.fail("Expected a Magentic Team");
  assert.equal(remaining.nodes[0].members[0].name, "Second");
  assert.equal(remaining.nodes[0].members[0].isManager, true);
});

test("a connection to Input, to the same node or in an existing direction is rejected", () => {
  const input = createEmptyDocument().nodes[0];
  const agent = createNode("agent", { x: 200, y: 0 });
  const document = {
    ...createEmptyDocument(),
    nodes: [input, agent],
    edges: [createEdge(createEmptyDocument(), "input", agent.nodeId)],
  };

  assert.equal(isValidConnection(document, agent.nodeId, "input"), false);
  assert.equal(isValidConnection(document, agent.nodeId, agent.nodeId), false);
  assert.equal(isValidConnection(document, "input", agent.nodeId), false);
  assert.equal(isValidConnection(document, "missing", agent.nodeId), false);
});

test("a new edge from a switch node takes the next order and has no conditions", () => {
  const document = createEmptyDocument();
  const switchNode = createNode("agent", { x: 200, y: 0 });
  const target = createNode("output", { x: 500, y: 0 });
  switchNode.routingStrategy = "switch";
  const graph: AgentflowEditorDocument = {
    ...document,
    nodes: [...document.nodes, switchNode, target],
    edges: [
      {
        edgeId: "existing",
        sourceNodeId: switchNode.nodeId,
        targetNodeId: target.nodeId,
        isFanInBarrier: false,
        isSwitchDefault: false,
        switchCaseOrder: 2,
        choiceId: null,
        label: null,
        conditions: [{ field: "contains", value: "ready", path: "" }],
      },
    ],
  };
  const next = createEdge(graph, switchNode.nodeId, "input");

  assert.equal(next.switchCaseOrder, 3);
  assert.deepEqual(next.conditions, []);
});

test("a new edge from a Human Gate binds the first choice without an edge", () => {
  const document = createEmptyDocument();
  const gate = createNode("humanGate", { x: 200, y: 0 });
  const target = createNode("output", { x: 500, y: 0 });
  const graph = { ...document, nodes: [...document.nodes, gate, target] };
  const next = createEdge(graph, gate.nodeId, target.nodeId);

  assert.equal(gate.kind, "humanGate");
  if (gate.kind !== "humanGate") assert.fail("Expected a Human Gate");
  assert.equal(next.choiceId, gate.choices[0].choiceId);
  assert.deepEqual(next.conditions, []);
});

test("search matches title, kind label and member name and returns at most eight", () => {
  const document = createEmptyDocument();
  const nodes = Array.from({ length: 9 }, (_, index) => ({
    ...createNode("agent", { x: index * 40, y: 0 }),
    name: `Agent ${index}`,
  }));
  const team = createNode("magenticBlock", { x: 0, y: 200 });
  const withMember = addMember(
    { ...document, nodes: [document.nodes[0], ...nodes, team] },
    team.nodeId,
    { kind: "workflowAsAgent", relateId: "workflow", name: "Review Workflow" },
  );

  assert.equal(searchNodes(withMember, "agent").length, 8);
  assert.equal(searchNodes(withMember, "magentic team")[0].nodeId, team.nodeId);
  assert.equal(searchNodes(withMember, "review workflow")[0].nodeId, team.nodeId);
});

test("inserting a node keeps the edge fields on the first edge", () => {
  const document = createEmptyDocument();
  const agent = createNode("agent", { x: 200, y: 0 });
  const output = createNode("output", { x: 500, y: 0 });
  const graph = { ...document, nodes: [...document.nodes, agent, output] };
  const edge = {
    ...createEdge(graph, "input", output.nodeId),
    label: "Approved",
    conditions: [{ field: "contains" as const, value: "yes", path: "" }],
  };
  const next = insertNodeOnEdge({ ...graph, edges: [edge] }, edge.edgeId, agent);

  assert.equal(next.edges[0].targetNodeId, agent.nodeId);
  assert.equal(next.edges[0].label, "Approved");
  assert.deepEqual(next.edges[0].conditions, edge.conditions);
  assert.equal(next.edges[1].sourceNodeId, agent.nodeId);
  assert.equal(next.edges[1].targetNodeId, output.nodeId);
});

test("inserting on a waiting edge keeps the wait on the second edge", () => {
  const document = createEmptyDocument();
  const agent = createNode("agent", { x: 200, y: 0 });
  const output = createNode("output", { x: 500, y: 0 });
  const graph = { ...document, nodes: [...document.nodes, agent, output] };
  const edge = { ...createEdge(graph, "input", output.nodeId), isFanInBarrier: true };
  const next = insertNodeOnEdge({ ...graph, edges: [edge] }, edge.edgeId, agent);

  assert.equal(next.edges[0].isFanInBarrier, false);
  assert.equal(next.edges[1].isFanInBarrier, true);
  assert.deepEqual(next.edges[0].conditions, []);
  assert.equal(next.edges[0].choiceId, null);
});

test("retargeting changes only the edge target", () => {
  const document = createEmptyDocument();
  const original = createEdge(document, "input", "target");
  const next = retargetEdge({ ...document, edges: [original] }, original.edgeId, "next");

  assert.equal(next.edges[0].targetNodeId, "next");
  assert.equal(next.edges[0].edgeId, original.edgeId);
  assert.equal(next.edges[0].sourceNodeId, original.sourceNodeId);
});
