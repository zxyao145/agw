export type { A2AEndpointDto, AgentDto } from "../../../../types/agentflow";
import type { components } from "@agw/api";
import type { ToolValueObject } from "@agw/tools";

export const AgentType = {
  System: 0,
  External: 1,
} as const;

export type AgentType = (typeof AgentType)[keyof typeof AgentType];

export const ExternalAgentKind = {
  None: 0,
  ClaudeCode: 1,
  Codex: 2,
  Pi: 3,
  A2A: 4,
  ACP: 5,
} as const;

export type ExternalAgentKind = (typeof ExternalAgentKind)[keyof typeof ExternalAgentKind];

export type A2AOriginCredentialsRequest = components["schemas"]["A2AOriginCredentialsRequest"];
export type A2AEndpointRequest = components["schemas"]["A2AEndpointRequest"];

export interface ExternalAgentOptionDto {
  kind: ExternalAgentKind;
  displayName: string;
  defaultExtra: string;
  supportedProviderTypes: string[];
}

export interface AgentCreateRequest {
  displayName: string;
  name: string;
  description: string;
  systemPrompt: string;
  modelProviderId: string | null;
  summaryModelProviderId: string | null;
  enableSummary: boolean;
  tools: ToolValueObject[];
  mcpToolServerIds?: string[] | null;
  skillIds?: string[] | null;
  connectionIds?: string[] | null;
  environmentVariables: Record<string, string>;
  type: AgentType;
  externalAgentKind: ExternalAgentKind;
  extra: string | null;
  responseSchema: string | null;
  a2aEndpoint?: A2AEndpointRequest;
}

export interface SystemAgentUpdateRequest {
  displayName: string;
  description: string;
  systemPrompt: string;
  modelProviderId: string | null;
  summaryModelProviderId: string | null;
  enableSummary: boolean;
  tools: ToolValueObject[];
  mcpToolServerIds?: string[] | null;
  skillIds?: string[] | null;
  connectionIds?: string[] | null;
  extra: string | null;
  environmentVariables: Record<string, string>;
  responseSchema?: string | null;
}

export interface ExternalAgentUpdateRequest {
  displayName?: string;
  description?: string;
  modelProviderId?: string | null;
  extra?: string | null;
  environmentVariables?: Record<string, string> | null;
  responseSchema?: string | null;
  a2aEndpoint?: A2AEndpointRequest;
}

export type AgentUpdateRequest = SystemAgentUpdateRequest | ExternalAgentUpdateRequest;

export type { McpToolServerDto, SkillDto } from "@agw/integrations";
export type { ToolLiteInfo, ToolValueObject } from "@agw/tools";
export type { ModelProviderDto } from "../../../../types/agentflow";
