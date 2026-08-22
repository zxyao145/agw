import type { AiMessage } from "@agw/api";

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function getAttributionSourceType(value: unknown): string | null {
  if (typeof value === "string") return value.split(":", 1)[0]?.trim() || null;
  if (!isRecord(value)) return null;

  const sourceType = value.sourceType ?? value.SourceType;
  if (typeof sourceType === "string") return sourceType.trim() || null;
  if (!isRecord(sourceType)) return null;

  const sourceTypeValue = sourceType.value ?? sourceType.Value;
  return typeof sourceTypeValue === "string" ? sourceTypeValue.trim() || null : null;
}

export function isInjectedContextMessage(message: AiMessage): boolean {
  return (
    getAttributionSourceType(message.additionalProperties?._attribution) === "AIContextProvider"
  );
}
