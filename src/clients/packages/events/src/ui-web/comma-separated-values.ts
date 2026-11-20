export type CommaSeparatedInput = string[] | string;

export function readCommaSeparatedValues(values: CommaSeparatedInput): string[] {
  if (Array.isArray(values)) return values;
  return values
    .split(",")
    .map((value) => value.trim())
    .filter(Boolean);
}

export function formatCommaSeparatedValues(values: CommaSeparatedInput): string {
  return Array.isArray(values) ? values.join(", ") : values;
}
