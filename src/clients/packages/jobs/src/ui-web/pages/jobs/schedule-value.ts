import { parse, pattern } from "iso8601-duration";

export const SCHEDULE_KIND_ONCE = "Once";
export const SCHEDULE_KIND_INTERVAL = "Interval";
export const SCHEDULE_KIND_CRON = "Cron";
export const JOB_INTERVAL_STEP_SECONDS = 30;

export type JobSchedule = {
  kind: string;
  at?: string | null;
  interval?: string | null;
  cron?: string | null;
};

function parsePositiveIsoDuration(value?: string | null): ReturnType<typeof parse> | null {
  if (!value?.trim()) {
    return null;
  }

  const durationText = value.trim();
  const match = pattern.exec(durationText);
  if (!match || match.index !== 0 || match[0] !== durationText) {
    return null;
  }

  try {
    const duration = parse(durationText);
    if (
      durationText.includes("W") ||
      durationText.includes(",") ||
      durationText.endsWith("T") ||
      [duration.years, duration.months, duration.days, duration.hours, duration.minutes].some(
        (unit) => unit !== undefined && !Number.isInteger(unit),
      )
    ) {
      return null;
    }
    return Object.values(duration).some((unit) => unit > 0) ? duration : null;
  } catch (error) {
    if (error instanceof RangeError) {
      return null;
    }
    throw error;
  }
}

export function isPositiveIsoDuration(value?: string | null): value is string {
  return parsePositiveIsoDuration(value) !== null;
}

function formatJobInterval(totalSeconds: number) {
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor(totalSeconds / 60) % 60;
  const seconds = totalSeconds % 60;
  const value = [hours, minutes, seconds].map((part) => String(part).padStart(2, "0")).join(":");
  const isoHours = hours ? `${hours}H` : "";
  const isoMinutes = minutes ? `${minutes}M` : "";
  const isoSeconds = seconds ? `${seconds}S` : "";
  return { value, isoDuration: `PT${isoHours}${isoMinutes}${isoSeconds}` };
}

export function formatSavedJobInterval(value?: string | null): string {
  const duration = parsePositiveIsoDuration(value);
  if (!value || !duration) return value ?? "";

  const { years = 0, months = 0, days = 0, hours = 0, minutes = 0, seconds = 0 } = duration;
  if (!Number.isInteger(seconds)) return value;

  // 与 XmlConvert.ToTimeSpan 的年份和月份转换规则一致。
  // Match the year and month conversion in XmlConvert.ToTimeSpan.
  const totalDays = (years + Math.floor(months / 12)) * 365 + (months % 12) * 30 + days;
  const totalSeconds = (totalDays * 24 + hours) * 3600 + minutes * 60 + seconds;
  return Number.isSafeInteger(totalSeconds) ? formatJobInterval(totalSeconds).value : value;
}

export function normalizeJobIntervalInput(value: string) {
  const match = /^([0-9]{2,}):([0-5][0-9]):([0-5][0-9])$/u.exec(value);
  if (!match || match[0] !== value) return null;

  const [, hours, minutes, seconds] = match;
  const totalSeconds = Number(hours) * 3600 + Number(minutes) * 60 + Number(seconds);
  const roundedSeconds = Math.max(
    JOB_INTERVAL_STEP_SECONDS,
    Math.round(totalSeconds / JOB_INTERVAL_STEP_SECONDS) * JOB_INTERVAL_STEP_SECONDS,
  );
  if (!Number.isSafeInteger(totalSeconds) || !Number.isSafeInteger(roundedSeconds)) return null;

  return { ...formatJobInterval(roundedSeconds), adjusted: totalSeconds !== roundedSeconds };
}

export function isCronValue(value?: string | null): value is string {
  if (!value?.trim()) {
    return false;
  }
  return value.trim().split(/\s+/u).length === 5;
}

export function getScheduleError(schedule: JobSchedule): string | null {
  switch (schedule.kind) {
    case SCHEDULE_KIND_ONCE:
      return schedule.at && !Number.isNaN(Date.parse(schedule.at))
        ? null
        : "Choose a valid run time.";
    case SCHEDULE_KIND_INTERVAL:
      return isPositiveIsoDuration(schedule.interval)
        ? null
        : "Enter a duration in HH:mm:ss, for example 00:01:00.";
    case SCHEDULE_KIND_CRON:
      return isCronValue(schedule.cron)
        ? null
        : "Enter a standard five-field cron expression such as */1 * * * *.";
    default:
      return "Select a schedule kind.";
  }
}

export function getDeliveryStateLabel(state: number): string {
  switch (state) {
    case 0:
    case 1:
    case 2:
    case 5:
      return "Running";
    case 3:
      return "Succeeded";
    case 4:
      return "Retrying";
    case 6:
      return "Failed";
    case 7:
      return "Cancelled";
    default:
      return `Unknown (${state})`;
  }
}

export function getNextRunState(job: {
  enabled: boolean;
  scheduleCompleted: boolean;
  schedule: Pick<JobSchedule, "kind">;
  dueAt?: string | null;
}): "ended" | "empty" | "scheduled" {
  if (job.schedule.kind === SCHEDULE_KIND_ONCE && job.scheduleCompleted) {
    return "ended";
  }
  if (!job.enabled || !job.dueAt) {
    return "empty";
  }
  return "scheduled";
}
