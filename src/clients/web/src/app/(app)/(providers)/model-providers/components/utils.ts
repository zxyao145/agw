import type { ModelProviderApiKeyDto } from "./types";
import { apiRequest } from "@/api/client";

export function parseIntOrNull(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed.length) return null;
  const n = Number(trimmed);
  if (!Number.isFinite(n)) return null;
  return Math.trunc(n);
}

export function parseFloatOrNull(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed.length) return null;
  const n = Number(trimmed);
  if (!Number.isFinite(n)) return null;
  return n;
}

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
