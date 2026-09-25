export const TRIGGER_TYPE_ONCE = 1;
export const TRIGGER_TYPE_INTERVAL = 2;
export const TRIGGER_TYPE_CRON = 3;

export function isPositiveIntervalValue(value?: string | null): value is string {
  if (!value) {
    return false;
  }

  const match = value.trim().match(/^(?:(\d+)\.)?(\d{1,2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?$/);
  if (!match) {
    return false;
  }

  const days = Number(match[1] ?? 0);
  const hours = Number(match[2]);
  const minutes = Number(match[3]);
  const seconds = Number(match[4] ?? 0);
  const totalMilliseconds = (((days * 24 + hours) * 60 + minutes) * 60 + seconds) * 1000;

  return totalMilliseconds > 0;
}

export function isCronValue(value?: string | null): value is string {
  if (!value) {
    return false;
  }

  return value.trim().split(/\s+/).length === 5;
}

/**
 * 返回 Interval 或 Cron 触发值的格式错误；Server 仍会校验 Cron 各字段的取值。
 * Returns the format error of an Interval or Cron trigger value; the Server still validates Cron field values.
 */
export function getTriggerValueError(triggerType: number, triggerValue: string): string | null {
  switch (triggerType) {
    case TRIGGER_TYPE_INTERVAL:
      return isPositiveIntervalValue(triggerValue)
        ? null
        : "Enter a positive .NET TimeSpan value such as 00:01:00.";
    case TRIGGER_TYPE_CRON:
      return isCronValue(triggerValue)
        ? null
        : "Enter a standard five-field cron string such as */5 * * * *.";
    default:
      return null;
  }
}
