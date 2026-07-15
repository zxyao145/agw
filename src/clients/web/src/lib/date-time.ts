const TIME_ZONE_SUFFIX_PATTERN = /(?:z|[+-]\d{2}:?\d{2})$/i;
const DATE_TIME_PATTERN =
  /^\d{4}-\d{2}-\d{2}t\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:z|[+-]\d{2}:?\d{2})?$/i;
const TIME_INPUT_PATTERN = /^(?:([01]\d|2[0-3])):([0-5]\d)(?::([0-5]\d))?$/;
const padDateTimeComponent = (value: number) => String(value).padStart(2, "0");

export function parseApiDateTime(value: string): Date | null {
  const normalizedValue = value.trim();
  if (!DATE_TIME_PATTERN.test(normalizedValue)) {
    return null;
  }

  const timestamp = TIME_ZONE_SUFFIX_PATTERN.test(normalizedValue)
    ? normalizedValue
    : `${normalizedValue}Z`;
  const date = new Date(timestamp);

  return Number.isNaN(date.getTime()) ? null : date;
}

export function formatLocalDateExact(date: Date): string {
  return `${date.getFullYear()}-${padDateTimeComponent(date.getMonth() + 1)}-${padDateTimeComponent(date.getDate())}`;
}

export function formatLocalTimeExact(date: Date): string {
  return `${padDateTimeComponent(date.getHours())}:${padDateTimeComponent(date.getMinutes())}:${padDateTimeComponent(date.getSeconds())}`;
}

export function formatLocalDateTimeExact(date: Date): string {
  return `${formatLocalDateExact(date)} ${formatLocalTimeExact(date)}`;
}

export function replaceLocalDate(dateTime: Date, selectedDate: Date): Date {
  return new Date(
    selectedDate.getFullYear(),
    selectedDate.getMonth(),
    selectedDate.getDate(),
    dateTime.getHours(),
    dateTime.getMinutes(),
    dateTime.getSeconds(),
    dateTime.getMilliseconds(),
  );
}

export function replaceLocalTime(dateTime: Date, time: string): Date | null {
  const match = TIME_INPUT_PATTERN.exec(time);
  if (!match) {
    return null;
  }

  return new Date(
    dateTime.getFullYear(),
    dateTime.getMonth(),
    dateTime.getDate(),
    Number(match[1]),
    Number(match[2]),
    Number(match[3] ?? 0),
  );
}

export function formatLocalDateTime(value?: string | null): string {
  if (!value) {
    return "-";
  }

  const date = parseApiDateTime(value);
  return date ? formatLocalDateTimeExact(date) : value;
}

export function formatFriendlyLocalDateTime(value: string, now = new Date()): string {
  const date = parseApiDateTime(value);
  if (!date) {
    return value;
  }

  const diffMs = now.getTime() - date.getTime();
  if (diffMs >= 0) {
    const diffMinutes = Math.floor(diffMs / 60_000);
    if (diffMinutes < 1) return "Just now";
    if (diffMinutes < 60) return `${diffMinutes}m ago`;

    const diffHours = Math.floor(diffMs / 3_600_000);
    if (diffHours < 24) return `${diffHours}h ago`;
  }

  return formatLocalDateTimeExact(date);
}
