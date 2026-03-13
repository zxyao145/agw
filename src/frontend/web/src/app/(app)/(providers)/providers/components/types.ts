import type { components } from "@/api/openapi";

type OpenApiProviderCreateRequest =
  components["schemas"]["ProviderCreateRequest"];

export type ProviderType =
  | "OpenAI"
  | "Anthropic"
  | "GoogleGemini"
  | "GitHubCopilot";

export type ProviderCreateRequest = Omit<
  OpenApiProviderCreateRequest,
  "providerType"
> & {
  providerType: ProviderType;
};

export type ProviderDto = {
  id: string;
  name: string;
  providerType: ProviderType;
  description: string | null;
  endpoint: string;
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
};
