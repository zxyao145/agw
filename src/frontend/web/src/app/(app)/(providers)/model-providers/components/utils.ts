import { ApiError, apiGet } from "@/api/client"
import type { ModelProviderApiKeyDto } from "./types"

export function getApiErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    if (typeof error.body === "string" && error.body.trim().length) {
      return error.body
    }
    return `${error.status} ${error.statusText}`
  }
  if (error instanceof Error) return error.message
  return "Unknown error"
}

export function parseIntOrNull(value: string): number | null {
  const trimmed = value.trim()
  if (!trimmed.length) return null
  const n = Number(trimmed)
  if (!Number.isFinite(n)) return null
  return Math.trunc(n)
}

export function parseFloatOrNull(value: string): number | null {
  const trimmed = value.trim()
  if (!trimmed.length) return null
  const n = Number(trimmed)
  if (!Number.isFinite(n)) return null
  return n
}

export async function listKeysByPair(args: {
  modelProviderId: string
}): Promise<ModelProviderApiKeyDto[]> {
  // NOTE: openapi-typescript didn't generate query param types for this endpoint
  // so we narrow the typing at the boundary.
  return (await apiGet("/api/model-provider-keys", {
    params: {
      query: { modelProviderId: args.modelProviderId },
    },
  } as never)) as unknown as ModelProviderApiKeyDto[];
}
