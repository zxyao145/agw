export type { AgentDto } from "../../../../types/agentflow";
import type { ToolValueObject } from "@agw/tools";

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
}

export interface ExternalAgentUpdateRequest {
  displayName?: string;
  description?: string;
  modelProviderId?: string | null;
  extra?: string | null;
  environmentVariables?: Record<string, string> | null;
}

export type AgentUpdateRequest = SystemAgentUpdateRequest | ExternalAgentUpdateRequest;

export type { McpToolServerDto, SkillDto } from "@agw/integrations";
export type { ToolInfo, ToolValueObject } from "@agw/tools";
export type { ModelProviderDto } from "../../../../types/agentflow";
