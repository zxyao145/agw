export type { AgentDto } from "@/types/agentflow";

export interface AgentCreateRequest {
  displayName: string;
  name: string;
  description: string;
  systemPrompt: string;
  modelProviderId: string | null;
  tools: string | null;
  mcpToolServerIds?: string[] | null;
  skillIds?: string[] | null;
  appInstanceIds?: string[] | null;
  environmentVariables: Record<string, string>;
}

export interface AgentUpdateRequest {
  displayName: string;
  description: string;
  systemPrompt: string;
  modelProviderId: string | null;
  tools: string | null;
  mcpToolServerIds?: string[] | null;
  skillIds?: string[] | null;
  appInstanceIds?: string[] | null;
  extra: string | null;
  environmentVariables: Record<string, string>;
}

export type ToolInfo = {
  name: string;
  description: string;
  category: string;
  typeName: string;
  parameters: Array<{
    name: string;
    type: string;
    description?: string;
    isOptional: boolean;
  }>;
};

export type ModelProviderDto = {
  id: string;
  modelId: string;
  providerId: string;
  providerName: string;
  providerType: string;
  modelName: string;
};

export type McpToolServerDto = {
  id: string;
  name: string;
};

export type SkillDto = {
  id: string;
  name: string;
  description: string;
  agentIds: string[];
};
