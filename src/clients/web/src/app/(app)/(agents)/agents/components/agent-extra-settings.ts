export const AGENT_EXTRA_SETTINGS_ERROR = "Extra Settings must be a JSON object.";

export function getAgentExtraSettingsError(value: string): string | null {
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
    return AGENT_EXTRA_SETTINGS_ERROR;
  }

  return AGENT_EXTRA_SETTINGS_ERROR;
}

export function normalizeAgentExtraSettings(value: string): string | null {
  const error = getAgentExtraSettingsError(value);
  if (error) {
    throw new Error(error);
  }

  const normalized = value.trim();
  return normalized || null;
}
