export const AGENT_RESPONSE_SCHEMA_ERROR = "Response Schema must be a JSON object.";

export const AGENT_RESPONSE_SCHEMA_EXAMPLE =
  '{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}';

export function getAgentResponseSchemaError(value: string): string | null {
  const normalized = value.trim();
  if (!normalized) {
    return null;
  }

  try {
    const parsed = JSON.parse(normalized);
    if (parsed !== null && typeof parsed === "object" && !Array.isArray(parsed)) {
      return null;
    }
  } catch {
    return AGENT_RESPONSE_SCHEMA_ERROR;
  }

  return AGENT_RESPONSE_SCHEMA_ERROR;
}

export function normalizeAgentResponseSchema(value: string): string | null {
  const error = getAgentResponseSchemaError(value);
  if (error) {
    throw new Error(error);
  }

  const normalized = value.trim();
  return normalized || null;
}
