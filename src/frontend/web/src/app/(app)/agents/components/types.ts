import type { components } from "@/api/openapi";

export type AgentCreateRequest = components["schemas"]["AgentCreateRequest"];

export type AgentDto = {
  id: string;
  name: string;
  description: string;
  systemPrompt: string;
  modelProviderApiKeyId: string;
  tools?: string | null;
  type: number; // 0 = System, 1 = External
  extra?: string | null;
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
};

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

export type AgentExecuteRequest = {
  threadId: string | null;
  input: string;
};

export type AgentExecuteResponse = {
  threadId: string;
  messages: import("@/types").AiMessage[];
};
