import assert from "node:assert/strict";
import test from "node:test";

import {
  AgentflowEdgeKind,
  AgentflowNodeKind,
  type AgentDto,
  type AgentflowDetailDto,
  type AgentflowDto,
} from "../../types/agentflow";
import { createAgentCopyRequest, createAgentflowCopyRequest } from "./copy-requests";

const sourceAgent = {
  id: "agent-1",
  displayName: "Research Agent",
  name: "research-agent",
  description: "Researches a topic",
  systemPrompt: "Find reliable sources.",
  modelProviderId: "model-provider-1",
  summaryModelProviderId: "summary-provider-1",
  enableSummary: true,
  tools: [
    {
      kind: "tool",
      definition: {
        name: "web_search",
        options: {},
      },
    },
  ],
  type: 0,
  extra: "not copied",
  environmentVariables: {
    SEARCH_TOKEN: "secret",
  },
  agentMcpToolServers: [
    {
      agentId: "agent-1",
      mcpToolServerId: "mcp-1",
    },
  ],
  agentSkillRelations: [
    {
      agentId: "agent-1",
      skillId: "skill-1",
    },
  ],
  agentConnectionRelations: [
    {
      agentId: "agent-1",
      connectionId: "connection-1",
    },
  ],
} satisfies AgentDto;

test("createAgentCopyRequest copies all System Agent configuration with a new identity", () => {
  const request = createAgentCopyRequest(sourceAgent, "12345678-abcd-efab-cdef-1234567890ab");

  assert.deepEqual(request, {
    displayName: "Research Agent Copy",
    name: "research-agent-copy-12345678",
    description: "Researches a topic",
    systemPrompt: "Find reliable sources.",
    modelProviderId: "model-provider-1",
    summaryModelProviderId: "summary-provider-1",
    enableSummary: true,
    tools: sourceAgent.tools,
    mcpToolServerIds: ["mcp-1"],
    skillIds: ["skill-1"],
    connectionIds: ["connection-1"],
    environmentVariables: {
      SEARCH_TOKEN: "secret",
    },
  });
  assert.notStrictEqual(request.tools, sourceAgent.tools);
  assert.notStrictEqual(request.environmentVariables, sourceAgent.environmentVariables);
  assert.equal("id" in request, false);
  assert.equal("type" in request, false);
  assert.equal("extra" in request, false);
});

test("createAgentCopyRequest keeps generated names within the database length limit", () => {
  const request = createAgentCopyRequest(
    {
      ...sourceAgent,
      displayName: "D".repeat(200),
      name: "n".repeat(200),
    },
    "abcd-ef12-3456-7890",
  );

  assert.equal(request.displayName.length, 200);
  assert.equal(request.displayName.endsWith(" Copy"), true);
  assert.equal(request.name.length, 200);
  assert.equal(request.name.endsWith("-copy-abcdef12"), true);
});

test("createAgentCopyRequest represents empty relations as null", () => {
  const request = createAgentCopyRequest(
    {
      ...sourceAgent,
      agentMcpToolServers: [],
      agentSkillRelations: null,
      agentConnectionRelations: undefined,
    },
    "12345678",
  );

  assert.equal(request.mcpToolServerIds, null);
  assert.equal(request.skillIds, null);
  assert.equal(request.connectionIds, null);
});

test("createAgentflowCopyRequest copies the complete graph without source identity", () => {
  const source = {
    id: "agentflow-1",
    name: "Research Flow",
    description: "Coordinates research",
    systemPrompt: "",
    summaryModelProviderId: "summary-provider-1",
  } satisfies AgentflowDto;
  const details = {
    ...source,
    nodes: [
      {
        agentflowId: source.id,
        nodeId: "input",
        kind: AgentflowNodeKind.Input,
        relateId: null,
        name: "Input",
        positionJson: '{"x":10,"y":20}',
        instructions: "Start",
        configJson: '{"mode":"fast"}',
      },
    ],
    edges: [
      {
        agentflowId: source.id,
        edgeId: "edge-1",
        sourceNodeId: "input",
        targetNodeId: "output",
        kind: AgentflowEdgeKind.Direct,
        label: "Next",
        conditionJson: '{"when":true}',
        configJson: '{"priority":1}',
      },
    ],
  } satisfies AgentflowDetailDto;

  const request = createAgentflowCopyRequest(source, details);

  assert.deepEqual(request, {
    name: "Research Flow Copy",
    description: "Coordinates research",
    summaryModelProviderId: "summary-provider-1",
    nodes: [
      {
        nodeId: "input",
        kind: AgentflowNodeKind.Input,
        relateId: null,
        name: "Input",
        positionJson: '{"x":10,"y":20}',
        instructions: "Start",
        configJson: '{"mode":"fast"}',
      },
    ],
    edges: [
      {
        edgeId: "edge-1",
        sourceNodeId: "input",
        targetNodeId: "output",
        kind: AgentflowEdgeKind.Direct,
        label: "Next",
        conditionJson: '{"when":true}',
        configJson: '{"priority":1}',
      },
    ],
  });
  assert.equal("id" in request, false);
  assert.equal("agentflowId" in request.nodes[0], false);
  assert.equal("agentflowId" in request.edges[0], false);
});

test("createAgentflowCopyRequest keeps the copied name within the database length limit", () => {
  const source = {
    id: "agentflow-1",
    name: "F".repeat(200),
    description: null,
    systemPrompt: "",
    summaryModelProviderId: null,
  } satisfies AgentflowDto;
  const details = {
    ...source,
    nodes: [],
    edges: [],
  } satisfies AgentflowDetailDto;

  const request = createAgentflowCopyRequest(source, details);

  assert.equal(request.name.length, 200);
  assert.equal(request.name.endsWith(" Copy"), true);
});
