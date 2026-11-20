import { JOB_TIMER_SOURCE_PREFIX } from "@agw/api";

import { MANAGED_BY_AGW, MANAGED_BY_JOB } from "../managed-record";

export const EVENT_SOURCE_KIND = {
  timer: 0,
  webhook: 1,
  httpPoll: 2,
  provider: 3,
  publisher: 4,
} as const;

export function getSourceNamePrefix(kind: number, managedBy: string): string {
  if (kind === EVENT_SOURCE_KIND.timer)
    return managedBy === MANAGED_BY_JOB ? JOB_TIMER_SOURCE_PREFIX : "timer.";
  if (kind === EVENT_SOURCE_KIND.webhook) return "webhook.";
  if (kind === EVENT_SOURCE_KIND.httpPoll) return "http.";
  if (kind === EVENT_SOURCE_KIND.provider) return "ext.";
  if (kind === EVENT_SOURCE_KIND.publisher) return managedBy === MANAGED_BY_AGW ? "agw." : "ext.";
  throw new Error("Unknown event source kind.");
}

export function splitSourceName(name: string, prefix: string): string {
  if (!name.startsWith(prefix)) throw new Error("Source name prefix does not match its kind.");
  return name.slice(prefix.length);
}
