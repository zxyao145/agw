export function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function readConfigJson(value: string): Record<string, unknown> | null {
  if (!value.trim()) return {};

  try {
    const parsed = JSON.parse(value) as unknown;
    return isPlainObject(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

export function updateConfigJson(currentJson: string, update: Record<string, unknown>) {
  const config = readConfigJson(currentJson) ?? {};
  for (const [key, value] of Object.entries(update)) {
    if (shouldRemoveConfigValue(value)) {
      delete config[key];
    } else {
      config[key] = value;
    }
  }

  return Object.keys(config).length > 0 ? JSON.stringify(config, null, 2) : "";
}

export function shouldRemoveConfigValue(value: unknown) {
  if (value === undefined || value === null) return true;
  if (typeof value === "string" && value.trim() === "") return true;
  if (Array.isArray(value) && value.length === 0) return true;
  return false;
}

export function readString(value: unknown) {
  return typeof value === "string" ? value : "";
}

export function readStringArray(value: unknown) {
  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === "string")
    : [];
}
