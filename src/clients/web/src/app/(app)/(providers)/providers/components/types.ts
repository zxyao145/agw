import type { components } from "@/api/openapi";

type OpenApiProviderCreateRequest = components["schemas"]["ProviderCreateRequest"];

type OpenApiProviderUpdateRequest = components["schemas"]["ProviderUpdateRequest"];

export type ProviderType = components["schemas"]["ProviderType"];

export type ProviderAuthType = "ApiKey";

export type ProviderAuthConfigRequest = {
  authType: ProviderAuthType;
  apiKey: string | null;
  envKey: string | null;
  enable: boolean;
};

export type ProviderCreateRequest = Omit<
  OpenApiProviderCreateRequest,
  "providerType" | "authConfigs" | "modelNames"
> & {
  providerType: ProviderType;
  authConfigs: ProviderAuthConfigRequest[];
  modelNames: string[];
};

export type ProviderUpdateRequest = Omit<
  OpenApiProviderUpdateRequest,
  "providerType" | "authConfigs" | "modelNames"
> & {
  providerType: ProviderType;
  authConfigs: ProviderAuthConfigRequest[];
  modelNames: string[];
};

export type ProviderModelDiscoveryResponse = {
  modelNames: string[];
};

export type ProviderModelDto = {
  id: string;
  name: string;
};

export type ProviderModelRelationDto = {
  id: string;
  modelId: string;
  providerId: string;
};

export type ProviderAuthConfigDto = {
  id: string;
  providerId: string;
  authType: ProviderAuthType;
  apiKey: string | null;
  envKey: string | null;
  enable: boolean;
};

export type ProviderDto = {
  id: string;
  name: string;
  providerType: ProviderType;
  description: string | null;
  endpoint: string;
  authConfigs?: ProviderAuthConfigDto[];
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
};
