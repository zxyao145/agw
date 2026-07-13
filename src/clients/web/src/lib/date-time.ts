const TIME_ZONE_SUFFIX_PATTERN = /(?:z|[+-]\d{2}:?\d{2})$/i;
const padDateTimeComponent = (value: number) => String(value).padStart(2, "0");

export function parseApiDateTime(value: string): Date | null {
  const normalizedValue = value.trim();
  if (!normalizedValue) {
    return null;
  }

  const timestamp = TIME_ZONE_SUFFIX_PATTERN.test(normalizedValue)
    ? normalizedValue
    : `${normalizedValue}Z`;
  const date = new Date(timestamp);

  return Number.isNaN(date.getTime()) ? null : date;
}

export function formatLocalDateTimeExact(date: Date): string {
  return `${date.getFullYear()}-${padDateTimeComponent(date.getMonth() + 1)}-${padDateTimeComponent(date.getDate())} ${padDateTimeComponent(date.getHours())}:${padDateTimeComponent(date.getMinutes())}:${padDateTimeComponent(date.getSeconds())}`;
}

export function formatLocalDateTime(value?: string | null): string {
  if (!value) {
    return "-";
  }

  const date = parseApiDateTime(value);
  return date ? date.toLocaleString() : value;
}
