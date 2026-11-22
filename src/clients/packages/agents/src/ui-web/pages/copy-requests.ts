import type { components } from "@agw/api";
import type { AgentDto, AgentflowDetailResponse } from "../../types/agentflow";
import { AgentType, ExternalAgentKind, type AgentCreateRequest } from "./agents/components/types";
import {
  createA2AEndpointDraft,
  getA2ACreateSettings,
  isA2AAgent,
} from "./agents/components/a2a-endpoint";

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
  const isExternalAgent = agent.type === AgentType.External;
  const mcpToolServerIds =
    agent.agentMcpToolServers?.map((relation) => relation.mcpToolServerId) ?? [];
  const skillIds = agent.agentSkillRelations?.map((relation) => relation.skillId) ?? [];
  const connectionIds =
    agent.agentConnectionRelations?.map((relation) => relation.connectionId) ?? [];
  const settings = isA2AAgent(agent.type, agent.externalAgentKind)
    ? getA2ACreateSettings()
    : {
        modelProviderId: agent.modelProviderId,
        environmentVariables: { ...agent.environmentVariables },
        extra: isExternalAgent ? (agent.extra ?? null) : null,
        // API 会拒绝为 Pi Agent 配置的 response schema。
        // The API rejects response schemas configured for Pi agents.
        responseSchema:
          agent.externalAgentKind === ExternalAgentKind.Pi ? null : (agent.responseSchema ?? null),
      };

  return {
    displayName: appendCopySuffix(agent.displayName),
    name: createAgentCopyName(agent.name),
    description: agent.description,
    systemPrompt: isExternalAgent ? "" : agent.systemPrompt,
    summaryModelProviderId: isExternalAgent ? null : agent.summaryModelProviderId,
    enableSummary: isExternalAgent ? false : agent.enableSummary,
    tools: isExternalAgent ? [] : [...agent.tools],
    mcpToolServerIds: !isExternalAgent && mcpToolServerIds.length > 0 ? mcpToolServerIds : null,
    skillIds: !isExternalAgent && skillIds.length > 0 ? skillIds : null,
    connectionIds: !isExternalAgent && connectionIds.length > 0 ? connectionIds : null,
    type: agent.type,
    externalAgentKind: agent.externalAgentKind,
    ...settings,
  };
}

export function createA2ACopyEndpointDraft(agent: AgentDto) {
  return isA2AAgent(agent.type, agent.externalAgentKind)
    ? createA2AEndpointDraft(agent.a2aEndpoint, false)
    : null;
}

export function createAgentflowCopyRequest({
  agentflow,
  definition,
}: AgentflowDetailResponse): components["schemas"]["AgentflowCreateRequest"] {
  return {
    name: appendCopySuffix(agentflow.name),
    description: agentflow.description,
    definition,
  };
}
