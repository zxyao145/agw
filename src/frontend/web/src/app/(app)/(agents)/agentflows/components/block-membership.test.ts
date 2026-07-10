import assert from "node:assert/strict";
import test from "node:test";

import type { Edge, Node } from "reactflow";

const NodeKind = {
  Agent: 0,
  WorkflowAsAgent: 1,
  HumanGate: 3,
  ConcurrentBlock: 5,
  GroupChatBlock: 7,
  Output: 9,
} as const;

async function loadBlockMembership() {
  return await import("./block-membership" + ".ts");
}

type TestNodeData = {
  kind: number;
  title: string;
  configJson: string;
};

function node(id: string, kind: number, config: Record<string, unknown> = {}): Node<TestNodeData> {
  return {
    id,
    type: "dagNode",
    position: { x: 0, y: 0 },
    data: {
      kind,
      title: id,
      configJson: Object.keys(config).length > 0 ? JSON.stringify(config) : "",
    },
  };
}

function edge(id: string, source: string, target: string): Edge {
  return {
    id,
    source,
    target,
    data: { kind: 0 },
  };
}

test("createBlockMembership hides exclusive participants without edges", async () => {
  const { createBlockMembership, getVisibleEdges, getVisibleNodes } = await loadBlockMembership();
  const nodes = [
    node("input", NodeKind.HumanGate),
    node("block", NodeKind.ConcurrentBlock, {
      participantNodeIds: ["french", "spanish"],
    }),
    node("french", NodeKind.Agent),
    node("spanish", NodeKind.WorkflowAsAgent),
    node("output", NodeKind.Output),
  ];

  const membership = createBlockMembership(nodes, [
    edge("in", "input", "block"),
    edge("out", "block", "output"),
  ]);

  assert.deepEqual(Array.from(membership.hiddenParticipantIds).sort(), ["french", "spanish"]);
  assert.deepEqual(
    getVisibleNodes(nodes, membership).map((item) => item.id),
    ["input", "block", "output"],
  );
  assert.deepEqual(
    getVisibleEdges([edge("in", "input", "block"), edge("out", "block", "output")], membership).map(
      (item) => item.id,
    ),
    ["in", "out"],
  );
});

test("getBlockParticipantNodes returns current block participants for drill-in canvas", async () => {
  const { createBlockMembership, getBlockParticipantNodes } = await loadBlockMembership();
  const nodes = [
    node("input", NodeKind.HumanGate),
    node("block-a", NodeKind.ConcurrentBlock, {
      participantNodeIds: ["french", "spanish", "missing"],
    }),
    node("block-b", NodeKind.GroupChatBlock, {
      participantNodeIds: ["english"],
    }),
    node("french", NodeKind.Agent),
    node("spanish", NodeKind.WorkflowAsAgent),
    node("english", NodeKind.Agent),
    node("output", NodeKind.Output),
  ];

  const membership = createBlockMembership(nodes, [
    edge("in", "input", "block-a"),
    edge("out", "block-a", "output"),
  ]);

  assert.deepEqual(Array.from(membership.hiddenParticipantIds).sort(), [
    "english",
    "french",
    "spanish",
  ]);
  assert.deepEqual(
    getBlockParticipantNodes(nodes, "block-a").map((item) => item.id),
    ["french", "spanish"],
  );
});

test("getBlockParticipantEdges returns no workflow edges for block drill-in canvas", async () => {
  const { getBlockParticipantEdges } = await loadBlockMembership();
  const edges = [edge("external", "french", "output"), edge("root", "input", "block")];

  assert.deepEqual(
    getBlockParticipantEdges(edges, "block").map((item) => item.id),
    [],
  );
});

test("getNextBlockParticipantPosition stays near laid-out block members", async () => {
  const { getNextBlockParticipantPosition } = await loadBlockMembership();
  const block = {
    ...node("block", NodeKind.ConcurrentBlock, {
      participantNodeIds: ["french", "spanish"],
    }),
    position: { x: 4000, y: 3000 },
  };
  const french = { ...node("french", NodeKind.Agent), position: { x: 12, y: 12 } };
  const spanish = { ...node("spanish", NodeKind.Agent), position: { x: 12, y: 80 } };

  assert.deepEqual(getNextBlockParticipantPosition([block, french, spanish], "block"), {
    x: 272,
    y: 12,
  });
});

test("createBlockMembership keeps externally linked participants visible", async () => {
  const { createBlockMembership, getVisibleNodes } = await loadBlockMembership();
  const nodes = [
    node("block", NodeKind.ConcurrentBlock, {
      participantNodeIds: ["french", "spanish"],
    }),
    node("french", NodeKind.Agent),
    node("spanish", NodeKind.Agent),
    node("output", NodeKind.Output),
  ];
  const edges = [edge("external", "french", "output")];

  const membership = createBlockMembership(nodes, edges);

  assert.equal(membership.hiddenParticipantIds.has("spanish"), true);
  assert.equal(membership.hiddenParticipantIds.has("french"), false);
  assert.equal(membership.externallyLinkedParticipantIds.has("french"), true);
  assert.deepEqual(
    getVisibleNodes(nodes, membership).map((item) => item.id),
    ["block", "french", "output"],
  );
});

test("createBlockMembership keeps shared participants visible", async () => {
  const { createBlockMembership } = await loadBlockMembership();
  const nodes = [
    node("block-a", NodeKind.ConcurrentBlock, {
      participantNodeIds: ["shared"],
    }),
    node("block-b", NodeKind.GroupChatBlock, {
      participantNodeIds: ["shared", "solo"],
    }),
    node("shared", NodeKind.Agent),
    node("solo", NodeKind.Agent),
  ];

  const membership = createBlockMembership(nodes, []);

  assert.equal(membership.hiddenParticipantIds.has("solo"), true);
  assert.equal(membership.hiddenParticipantIds.has("shared"), false);
  assert.equal(membership.sharedParticipantIds.has("shared"), true);
});

test("updateBlockParticipantIds removes membership so the participant becomes visible", async () => {
  const { createBlockMembership, getVisibleNodes, updateBlockParticipantIds } =
    await loadBlockMembership();
  const block = node("block", NodeKind.ConcurrentBlock, {
    participantNodeIds: ["french"],
  });
  const nodes = [block, node("french", NodeKind.Agent)];
  const removedConfigJson = updateBlockParticipantIds(block.data.configJson, []);
  const updatedNodes = [
    {
      ...block,
      data: {
        ...block.data,
        configJson: removedConfigJson,
      },
    },
    nodes[1],
  ];

  const membership = createBlockMembership(updatedNodes, []);

  assert.equal(membership.hiddenParticipantIds.has("french"), false);
  assert.deepEqual(
    getVisibleNodes(updatedNodes, membership).map((item) => item.id),
    ["block", "french"],
  );
});

test("add and remove participant ids update block config", async () => {
  const { addBlockParticipantId, removeBlockParticipantId, updateBlockParticipantIds } =
    await loadBlockMembership();

  const withFirstMember = addBlockParticipantId("", "french");
  assert.deepEqual(JSON.parse(withFirstMember), {
    participantNodeIds: ["french"],
  });

  const withSecondMember = addBlockParticipantId(withFirstMember, "spanish");
  assert.deepEqual(JSON.parse(withSecondMember), {
    participantNodeIds: ["french", "spanish"],
  });

  const withoutFirstMember = removeBlockParticipantId(withSecondMember, "french");
  assert.deepEqual(JSON.parse(withoutFirstMember), {
    participantNodeIds: ["spanish"],
  });

  const withoutManager = updateBlockParticipantIds(
    JSON.stringify({
      participantNodeIds: ["french"],
      managerNodeId: "french",
    }),
    [],
  );
  assert.equal(withoutManager, "");
});
