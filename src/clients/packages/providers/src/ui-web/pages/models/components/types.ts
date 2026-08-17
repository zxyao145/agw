import type { components } from "@agw/api";

export type ModelCreateRequest = components["schemas"]["ModelCreateRequest"];
export type ModelUpdateRequest = components["schemas"]["ModelUpdateRequest"];

export const DEFAULT_MAX_CONTEXT_WINDOW_TOKENS = 256_000;
export const DEFAULT_MAX_OUTPUT_TOKENS = 64_000;

export type ModelDto = components["schemas"]["AgwAiModel"];

export function getModelTokenLimitError(
  maxContextWindowTokens: number,
  maxOutputTokens: number,
): string | null {
  if (!Number.isInteger(maxContextWindowTokens) || maxContextWindowTokens <= 0) {
    return "Context window must be a positive whole number.";
  }
  if (!Number.isInteger(maxOutputTokens) || maxOutputTokens <= 0) {
    return "Maximum output must be a positive whole number.";
  }
  if (maxOutputTokens >= maxContextWindowTokens) {
    return "Maximum output must be smaller than the context window.";
  }
  return null;
}
