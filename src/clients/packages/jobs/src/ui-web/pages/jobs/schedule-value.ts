import { parse, pattern } from "iso8601-duration";

export const SCHEDULE_KIND_ONCE = "Once";
export const SCHEDULE_KIND_INTERVAL = "Interval";
export const SCHEDULE_KIND_CRON = "Cron";

export type JobSchedule = {
  kind: string;
  at?: string | null;
  interval?: string | null;
  cron?: string | null;
};

export function isPositiveIsoDuration(value?: string | null): value is string {
  if (!value?.trim()) {
    return false;
  }

  const durationText = value.trim();
  const match = pattern.exec(durationText);
  if (!match || match.index !== 0 || match[0] !== durationText) {
    return false;
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
      return false;
    }
    return Object.values(duration).some((unit) => unit > 0);
  } catch (error) {
    if (error instanceof RangeError) {
      return false;
    }
    throw error;
  }
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
        : "Enter a positive ISO 8601 duration such as PT1M.";
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
