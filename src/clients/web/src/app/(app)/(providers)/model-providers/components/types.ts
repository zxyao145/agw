import type { components } from "@/api/openapi";

export type ModelProviderCreateRequest = components["schemas"]["ModelProviderCreateRequest"];

export type ModelProviderDto = {
  id: string;
  modelId: string;
  providerId: string;
  inputPrice: number;
  outputPrice: number;
  cacheRead: number;
  cacheWrite: number;
  rpsLimit: number;
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
};

export type ModelProviderApiKeyDto = {
  id: string;
  modelId: string;
  providerId: string;
  apiKey?: string | null;
  enable: boolean;
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
};

export type ModelDto = {
  id: string;
  name: string;
  description: string | null;
  maxTokens: number;
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
};

export type ProviderDto = {
  id: string;
  name: string;
  providerType: string;
  description: string | null;
  endpoint: string;
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
};
