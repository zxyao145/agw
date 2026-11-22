import type { ToolValueObject } from "@agw/tools";
import type { components } from "@agw/api";

type Schemas = components["schemas"];

export interface BaseDto {
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
}

export type A2AEndpointDto = components["schemas"]["A2AEndpointDto"];

export interface AgentDto extends BaseDto {
  id: string;
  displayName: string;
  name: string;
  enable: boolean;
  systemPrompt: string;
  modelProviderId: string | null;
  summaryModelProviderId: string | null;
  enableSummary: boolean;

  description: string;
  tools: ToolValueObject[];
  type: 0 | 1;
  externalAgentKind: 0 | 1 | 2 | 3 | 4;
  a2aEndpoint?: A2AEndpointDto | null;
  extra?: string | null;
  responseSchema?: string | null;
  resultFormat?: Schemas["ResultFormat"];
  environmentVariables: Record<string, string>;
  agentMcpToolServers?: Array<{
    agentId: string;
    mcpToolServerId: string;
  }> | null;
  agentSkillRelations?: Array<{
    agentId: string;
    skillId: string;
  }> | null;
  agentConnectionRelations?: Array<{
    agentId: string;
    connectionId: string;
  }> | null;
}

export interface ModelProviderDto {
  id: string;
  modelId: string;
  providerId: string;
  providerName: string;
  providerType: string;
  modelName: string;
}

export type AgentflowNodeKind = AgentflowNode["kind"];
export type AgentflowRoutingStrategy = Schemas["AgentflowRoutingStrategy"];
export type AgentflowConditionRule = Schemas["AgentflowConditionRuleDto"];
export type AgentflowPosition = { x: number; y: number };
export type AgentflowBlockMember = Schemas["AgentflowBlockMemberDto"];
export type AgentflowNode = Schemas["AgentflowNodeDto"];
export type AgentflowEdge = Schemas["AgentflowEdgeDto"];
export type AgentflowDefinition = Schemas["AgentflowDefinitionDto"];
export type AgentflowDto = Schemas["AgentflowDto"];
export type AgentflowDetailResponse = Schemas["AgentflowDetailResponse"];
export type AgentflowProblem = Schemas["AgentflowProblem"];
export type AgentflowValidateRequest = Schemas["AgentflowValidateRequest"];
export type AgentflowRevisionDto = Schemas["AgentflowRevisionDto"];
