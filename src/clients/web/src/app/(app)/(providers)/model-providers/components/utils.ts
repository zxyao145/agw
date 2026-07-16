import { apiRequest } from "@/api/client";

import type { ModelProviderApiKeyDto } from "./types";

export async function listKeysByPair(args: {
  modelProviderId: string;
}): Promise<ModelProviderApiKeyDto[]> {
  const request = apiRequest as unknown as (
    path: string,
    method: "get",
    options: { params: { query: { modelProviderId: string } } },
  ) => Promise<unknown>;

  return (await request("/api/model-provider-keys", "get", {
    params: { query: { modelProviderId: args.modelProviderId } },
  })) as ModelProviderApiKeyDto[];
}
