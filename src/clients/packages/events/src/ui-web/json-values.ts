import type { JsonValue } from "@agw/api";

export function readJsonObject(value: unknown): Record<string, JsonValue> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, JsonValue>)
    : null;
}

export function readStringArray(value: unknown): string[] | null {
  return Array.isArray(value) && value.every((item) => typeof item === "string") ? value : null;
}

export function tryParseJson(text: string): JsonValue | undefined {
  try {
    return JSON.parse(text) as JsonValue;
  } catch (error) {
    if (error instanceof SyntaxError) {
      console.warn(
        "Invalid JSON in an Events form field.",
        error.stack?.replace(error.message, "Invalid JSON"),
      );
      return undefined;
    }
    throw error;
  }
}
