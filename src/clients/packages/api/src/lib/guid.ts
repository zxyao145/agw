export const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function isNonEmptyGuid(value?: string | null): boolean {
  if (!value) {
    return false;
  }

  return value.toLowerCase() !== EMPTY_GUID && GUID_PATTERN.test(value);
}
