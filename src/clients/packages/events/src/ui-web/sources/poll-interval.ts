import { parse, pattern } from "iso8601-duration";

export type PollIntervalUnit = "minutes" | "hours" | "days";

export function toIsoInterval(value: number, unit: PollIntervalUnit): string {
  if (!Number.isSafeInteger(value) || value < 1) throw new Error("Poll interval must be positive.");
  if (unit === "minutes") return `PT${value}M`;
  if (unit === "hours") return `PT${value}H`;
  return `P${value}D`;
}

export function fromIsoInterval(value: string): { value: number; unit: PollIntervalUnit } | null {
  const match = pattern.exec(value);
  const hasIntegralUnits = value !== "P" && value !== "PT" && !/[.,]/.test(value);
  if (match?.[0] !== value || !hasIntegralUnits) return null;

  const duration = parse(value);
  for (const unit of ["minutes", "hours", "days"] as const) {
    const number = duration[unit];
    const isSupportedValue =
      number !== undefined &&
      Number.isSafeInteger(number) &&
      number > 0 &&
      toIsoInterval(number, unit) === value;
    if (isSupportedValue) return { value: number, unit };
  }
  return null;
}
