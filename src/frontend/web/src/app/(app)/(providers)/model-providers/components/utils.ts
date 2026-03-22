import { ApiError } from "@/api/client";
import type { ModelProviderApiKeyDto } from "./types";

export function getApiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (typeof error.body === "string" && error.body.trim().length) {
      return error.body;
    }
    return `${error.status} ${error.statusText}`;
  }
  if (error instanceof Error) return error.message;
  return "Unknown error";
}

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
  const params = new URLSearchParams({ modelProviderId: args.modelProviderId });
  const response = await fetch(`/api/model-provider-keys?${params.toString()}`);

  if (!response.ok) {
    throw new Error(`Failed to load model provider keys for ${args.modelProviderId}`);
  }

  return (await response.json()) as ModelProviderApiKeyDto[];
}
