export const EMPTY_EXTRA_SETTING_TEXT = "{\n  \n}";
export const CHAT_SETTINGS_DIALOG_CONTENT_CLASS_NAME = "max-h-[90vh] flex flex-col overflow-hidden";
export const CHAT_SETTINGS_DIALOG_BODY_CLASS_NAME = "flex-1 min-h-0 overflow-y-auto pr-1";

function sortJsonValue(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map((item) => sortJsonValue(item));
  }

  if (value && typeof value === "object") {
    return Object.fromEntries(
      Object.entries(value as Record<string, unknown>)
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([key, nestedValue]) => [key, sortJsonValue(nestedValue)]),
    );
  }

  return value;
}

function getJsonObjectSignature(value?: string | null): string | null {
  const parsed = tryParseJsonObjectText(value ?? "");
  if (parsed === null) {
    return null;
  }

  return JSON.stringify(sortJsonValue(parsed));
}

export function formatParsedJsonObjectText(value: Record<string, unknown>): string {
  return Object.keys(value).length === 0 ? EMPTY_EXTRA_SETTING_TEXT : JSON.stringify(value, null, 2);
}

export function formatJsonObjectText(value?: string | null): string {
  const trimmedValue = value?.trim();
  if (!trimmedValue) {
    return EMPTY_EXTRA_SETTING_TEXT;
  }

  const parsed = tryParseJsonObjectText(trimmedValue);
  if (parsed === null) {
    return trimmedValue;
  }

  return formatParsedJsonObjectText(parsed);
}

export function tryParseJsonObjectText(value: string): Record<string, unknown> | null {
  const trimmedValue = value.trim();
  if (!trimmedValue) {
    return {};
  }

  try {
    const parsed = JSON.parse(trimmedValue) as unknown;
    if (!parsed || Array.isArray(parsed) || typeof parsed !== "object") {
      return null;
    }

    return parsed as Record<string, unknown>;
  } catch {
    return null;
  }
}

export function normalizeExtraSettingTextForStorage(
  value: string,
  projectValue?: string | null,
): string | undefined {
  const trimmedValue = value.trim();
  if (!trimmedValue) {
    return undefined;
  }

  const parsed = tryParseJsonObjectText(value);
  if (parsed === null) {
    return undefined;
  }

  if (getJsonObjectSignature(value) === getJsonObjectSignature(projectValue)) {
    return undefined;
  }

  return formatParsedJsonObjectText(parsed);
}
