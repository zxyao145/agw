import type { components } from "@/api/openapi";

export type {AgentDto} from  "@/types/agentflow";


export type AgentCreateRequest = components["schemas"]["AgentCreateRequest"];

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

