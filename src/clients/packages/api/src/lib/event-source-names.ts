export const MAX_SOURCE_NAME_LENGTH = 200;
export const JOB_TIMER_SOURCE_PREFIX = "agw.timer.";

const SOURCE_SUFFIX_PATTERN = /^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$/;

export function isValidSourceSuffix(suffix: string): boolean {
  return SOURCE_SUFFIX_PATTERN.test(suffix) && !suffix.includes("..");
}
