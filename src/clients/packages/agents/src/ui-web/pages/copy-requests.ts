import type {
  AgentDto,
  AgentflowDetailDto,
  AgentflowDto,
  AgentflowSaveRequest,
} from "../../types/agentflow";
import type { AgentCreateRequest } from "./agents/components/types";

const MAX_DEFINITION_NAME_LENGTH = 200;
const COPY_DISPLAY_SUFFIX = " Copy";

function appendCopySuffix(value: string): string {
  const base = value.trimEnd();
  return `${base.slice(0, MAX_DEFINITION_NAME_LENGTH - COPY_DISPLAY_SUFFIX.length)}${COPY_DISPLAY_SUFFIX}`;
}

function createAgentCopyName(name: string): string {
  const copySuffix = "-copy";
  const base = name.trimEnd().slice(0, MAX_DEFINITION_NAME_LENGTH - copySuffix.length);
  return `${base}${copySuffix}`;
}

export function createAgentCopyRequest(agent: AgentDto): AgentCreateRequest {
  const isExternalAgent = agent.type === 1;
  const mcpToolServerIds =
    agent.agentMcpToolServers?.map((relation) => relation.mcpToolServerId) ?? [];
  const skillIds = agent.agentSkillRelations?.map((relation) => relation.skillId) ?? [];
  const connectionIds =
    agent.agentConnectionRelations?.map((relation) => relation.connectionId) ?? [];

  return {
    displayName: appendCopySuffix(agent.displayName),
    name: createAgentCopyName(agent.name),
    description: agent.description,
    systemPrompt: isExternalAgent ? "" : agent.systemPrompt,
    modelProviderId: agent.modelProviderId,
    summaryModelProviderId: isExternalAgent ? null : agent.summaryModelProviderId,
    enableSummary: isExternalAgent ? false : agent.enableSummary,
    tools: isExternalAgent ? [] : [...agent.tools],
    mcpToolServerIds: !isExternalAgent && mcpToolServerIds.length > 0 ? mcpToolServerIds : null,
    skillIds: !isExternalAgent && skillIds.length > 0 ? skillIds : null,
    connectionIds: !isExternalAgent && connectionIds.length > 0 ? connectionIds : null,
    environmentVariables: { ...agent.environmentVariables },
    type: agent.type,
    externalAgentKind: agent.externalAgentKind,
    extra: isExternalAgent ? (agent.extra ?? null) : null,
  };
}

export function createAgentflowCopyRequest(
  agentflow: AgentflowDto,
  details: AgentflowDetailDto,
): AgentflowSaveRequest {
  return {
    name: appendCopySuffix(agentflow.name),
    description: agentflow.description,
    summaryModelProviderId: agentflow.summaryModelProviderId,
    nodes: details.nodes.map((node) => ({
      nodeId: node.nodeId,
      kind: node.kind,
      relateId: node.relateId,
      name: node.name,
      positionJson: node.positionJson,
      instructions: node.instructions,
      configJson: node.configJson,
    })),
    edges: details.edges.map((edge) => ({
      edgeId: edge.edgeId,
      sourceNodeId: edge.sourceNodeId,
      targetNodeId: edge.targetNodeId,
      kind: edge.kind,
      label: edge.label,
      conditionJson: edge.conditionJson,
      configJson: edge.configJson,
    })),
  };
}
