import assert from "node:assert/strict";
import test from "node:test";

import type { AgentDto, AgentflowDetailResponse } from "../../types/agentflow";
import {
  createA2ACopyEndpointDraft,
  createAgentCopyRequest,
  createAgentflowCopyRequest,
} from "./copy-requests";

const sourceAgent = {
  id: "agent-1",
  displayName: "Research Agent",
  name: "research-agent",
  description: "Researches a topic",
  systemPrompt: "Find reliable sources.",
  modelProviderId: "model-provider-1",
  summaryModelProviderId: "summary-provider-1",
  enableSummary: true,
  enable: true,
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
  externalAgentKind: 0,
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
  const request = createAgentCopyRequest(sourceAgent);

  assert.deepEqual(request, {
    displayName: "Research Agent Copy",
    name: "research-agent-copy",
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
    type: 0,
    externalAgentKind: 0,
    extra: null,
    responseSchema: null,
  });
  assert.notStrictEqual(request.tools, sourceAgent.tools);
  assert.notStrictEqual(request.environmentVariables, sourceAgent.environmentVariables);
  assert.equal("id" in request, false);
  assert.equal(request.type, 0);
  assert.equal(request.externalAgentKind, 0);
  assert.equal(request.extra, null);
});

test("createAgentCopyRequest preserves External Agent kind and Extra Settings", () => {
  const request = createAgentCopyRequest({
    ...sourceAgent,
    name: "claude-reviewer",
    type: 1,
    externalAgentKind: 1,
    extra: '{"model":"claude-sonnet"}',
  });

  assert.equal(request.name, "claude-reviewer-copy");
  assert.equal(request.type, 1);
  assert.equal(request.externalAgentKind, 1);
  assert.equal(request.extra, '{"model":"claude-sonnet"}');
  assert.equal(request.systemPrompt, "");
  assert.equal(request.enableSummary, false);
  assert.equal(request.summaryModelProviderId, null);
  assert.deepEqual(request.tools, []);
  assert.equal(request.mcpToolServerIds, null);
  assert.equal(request.skillIds, null);
  assert.equal(request.connectionIds, null);
});

test("createAgentCopyRequest preserves the Response Schema for System and External agents", () => {
  const schema =
    '{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}';

  const systemCopy = createAgentCopyRequest({ ...sourceAgent, responseSchema: schema });
  assert.equal(systemCopy.responseSchema, schema);

  const externalCopy = createAgentCopyRequest({
    ...sourceAgent,
    type: 1,
    externalAgentKind: 1,
    responseSchema: schema,
  });
  assert.equal(externalCopy.responseSchema, schema);
});

test("createAgentCopyRequest drops the Response Schema for Pi agents", () => {
  const schema = '{"type":"object"}';

  const piCopy = createAgentCopyRequest({
    ...sourceAgent,
    type: 1,
    externalAgentKind: 3,
    responseSchema: schema,
  });
  assert.equal(piCopy.responseSchema, null);
});

test("an A2A copy prefills each origin and header name for new values", () => {
  const cardUrl = "https://card.example/.well-known/agent-card.json";
  const cardOrigin = "https://card.example:443";
  const interfaceOrigin = "https://agent.example:443";
  const agent: AgentDto = {
    ...sourceAgent,
    type: 1,
    externalAgentKind: 4,
    a2aEndpoint: {
      cardUrl,
      interfaceUrl: "https://agent.example/run",
      protocolBinding: "HTTP+JSON",
      streaming: false,
      extra: { card: {}, cardFetchedAt: "2026-11-21T00:00:00Z" },
      credentialHeaderNames: [
        { origin: cardOrigin, names: ["Authorization"] },
        { origin: interfaceOrigin, names: ["X-Tenant", "Authorization"] },
      ],
    },
  };

  assert.deepEqual(createA2ACopyEndpointDraft(agent), {
    cardUrl,
    groups: [
      {
        origin: cardOrigin,
        rows: [{ origin: cardOrigin, name: "Authorization", value: "", saved: false }],
      },
      {
        origin: interfaceOrigin,
        rows: [
          { origin: interfaceOrigin, name: "X-Tenant", value: "", saved: false },
          { origin: interfaceOrigin, name: "Authorization", value: "", saved: false },
        ],
      },
    ],
  });
  assert.deepEqual(createAgentCopyRequest(agent).environmentVariables, {});
});

test("createAgentCopyRequest keeps generated names within the database length limit", () => {
  const request = createAgentCopyRequest({
    ...sourceAgent,
    displayName: "D".repeat(200),
    name: "n".repeat(200),
  });

  assert.equal(request.displayName.length, 200);
  assert.equal(request.displayName.endsWith(" Copy"), true);
  assert.equal(request.name.length, 200);
  assert.equal(request.name.endsWith("-copy"), true);
});

test("createAgentCopyRequest represents empty relations as null", () => {
  const request = createAgentCopyRequest({
    ...sourceAgent,
    agentMcpToolServers: [],
    agentSkillRelations: null,
    agentConnectionRelations: undefined,
  });

  assert.equal(request.mcpToolServerIds, null);
  assert.equal(request.skillIds, null);
  assert.equal(request.connectionIds, null);
});

const sourceAgentflow = {
  agentflow: {
    id: "agentflow-1",
    name: "Research Flow",
    description: "Coordinates research",
    enable: true,
    latestRevision: 2,
    publishedRevision: 1,
    createTime: "2026-11-21T10:00:00Z",
    updateTime: "2026-11-21T11:00:00Z",
  },
  revision: 2,
  definition: {
    nodes: [
      {
        nodeId: "input",
        kind: "input",
        name: "Input",
        position: { x: 10, y: 20 },
        routingStrategy: "direct",
      },
    ],
    edges: [
      {
        edgeId: "edge-1",
        sourceNodeId: "input",
        targetNodeId: "output",
        label: "Next",
        isFanInBarrier: false,
        isSwitchDefault: false,
        switchCaseOrder: 0,
        choiceId: null,
        conditions: [],
      },
    ],
  },
} satisfies AgentflowDetailResponse;

test("createAgentflowCopyRequest copies the complete graph without source identity", () => {
  const request = createAgentflowCopyRequest(sourceAgentflow);

  assert.deepEqual(request, {
    name: "Research Flow Copy",
    description: "Coordinates research",
    definition: sourceAgentflow.definition,
  });
  assert.equal("id" in request, false);
});

test("createAgentflowCopyRequest keeps the copied name within the database length limit", () => {
  const request = createAgentflowCopyRequest({
    ...sourceAgentflow,
    agentflow: { ...sourceAgentflow.agentflow, name: "F".repeat(200) },
  });

  assert.equal(request.name.length, 200);
  assert.equal(request.name.endsWith(" Copy"), true);
});
