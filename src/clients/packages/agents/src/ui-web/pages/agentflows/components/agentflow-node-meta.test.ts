import assert from "node:assert/strict";
import test from "node:test";

import type { AgentDto } from "../../../../types/agentflow";
import { AGENTFLOW_NODE_META, describeNode } from "./agentflow-node-meta";
import { createNode } from "./agentflow-document";

const lookups = {
  agents: [],
  agentflows: [],
  modelProviders: [],
};

test("every node kind has a label, an icon and a description", () => {
  for (const meta of Object.values(AGENTFLOW_NODE_META)) {
    assert.ok(meta.label);
    assert.ok(meta.icon);
    assert.ok(meta.description);
  }
});

test("describeNode gives the second line for every kind", () => {
  const samples = [
    [createNode("input", { x: 0, y: 0 }), "User message starts the flow"],
    [createNode("agent", { x: 0, y: 0 }), "No agent selected"],
    [createNode("workflowAsAgent", { x: 0, y: 0 }), "Nested Agentflow"],
    [createNode("promptAdapter", { x: 0, y: 0 }), "No instructions yet"],
    [createNode("clearMessages", { x: 0, y: 0 }), "Continues with empty input"],
    [createNode("humanGate", { x: 0, y: 0 }), "Continue"],
    [createNode("checkpointMarker", { x: 0, y: 0 }), "Checkpoint"],
    [createNode("output", { x: 0, y: 0 }), "Returns the incoming messages"],
    [createNode("concurrentBlock", { x: 0, y: 0 }), "0 members"],
    [createNode("handoffBlock", { x: 0, y: 0 }), "0 members"],
    [createNode("groupChatBlock", { x: 0, y: 0 }), "0 members"],
    [createNode("magenticBlock", { x: 0, y: 0 }), "0 members"],
  ] as const;

  for (const [node, expected] of samples) {
    assert.equal(describeNode(node, lookups), expected);
  }

  const externalAgent = {
    id: "external-agent",
    name: "External",
    displayName: "External",
    enable: true,
    systemPrompt: "",
    modelProviderId: null,
    summaryModelProviderId: null,
    enableSummary: false,
    description: "",
    tools: [],
    type: 1,
    externalAgentKind: 0,
    environmentVariables: {},
  } satisfies AgentDto;
  const linkedAgent = createNode(
    "agent",
    { x: 0, y: 0 },
    {
      kind: "agent",
      relateId: externalAgent.id,
      name: externalAgent.name,
    },
  );

  assert.equal(
    describeNode(linkedAgent, { ...lookups, agents: [externalAgent] }),
    "External agent",
  );
});
