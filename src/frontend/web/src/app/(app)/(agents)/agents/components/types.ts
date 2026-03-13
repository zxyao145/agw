export type {AgentDto} from  "@/types/agentflow";


export interface AgentCreateRequest {
  displayName: string;
  name: string;
  description: string;
  systemPrompt: string;
  modelProviderApiKeyId: string | null;
  tools?: string | null;
  mcpToolServerIds?: string[] | null;
}

export interface AgentUpdateRequest {
  displayName: string;
  description: string;
  systemPrompt: string;
  modelProviderApiKeyId: string | null;
  tools?: string | null;
  mcpToolServerIds?: string[] | null;
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

export type ModelProviderApiKeyDto = {
  id: string;
  apiKeyName: string;
  modelProviderId: string;
  modelId: string;
  providerId: string;
  providerName: string;
  modelName: string;
};

export type McpToolServerDto = {
  id: string;
  name: string;
};
