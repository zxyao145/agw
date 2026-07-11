import type { components } from "@/api/openapi";

type OpenApiProviderCreateRequest = components["schemas"]["ProviderCreateRequest"];

type OpenApiProviderUpdateRequest = components["schemas"]["ProviderUpdateRequest"];

export type ProviderType = components["schemas"]["ProviderType"];

export type ProviderAuthType = "ApiKey" | "EnvVariable";

export type ProviderAuthConfigRequest = {
  authType: ProviderAuthType;
  apiKey: string | null;
  envKey: string | null;
  enable: boolean;
};

export type ProviderCreateRequest = Omit<
  OpenApiProviderCreateRequest,
  "providerType" | "authConfigs"
> & {
  providerType: ProviderType;
  authConfigs: ProviderAuthConfigRequest[];
};

export type ProviderUpdateRequest = Omit<
  OpenApiProviderUpdateRequest,
  "providerType" | "authConfigs"
> & {
  providerType: ProviderType;
  authConfigs: ProviderAuthConfigRequest[];
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
