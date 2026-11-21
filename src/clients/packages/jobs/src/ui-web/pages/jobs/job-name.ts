import { isValidSourceSuffix, JOB_TIMER_SOURCE_PREFIX, MAX_SOURCE_NAME_LENGTH } from "@agw/api";

const MAX_JOB_NAME_LENGTH = MAX_SOURCE_NAME_LENGTH - JOB_TIMER_SOURCE_PREFIX.length;
export const JOB_NAME_DESCRIPTION =
  "Use lowercase letters, digits, '-' and '.', beginning and ending with a letter or digit.";

export function isValidJobName(name: string): boolean {
  return name.length <= MAX_JOB_NAME_LENGTH && isValidSourceSuffix(name);
}

export function getJobNameError(name: string): string | null {
  const validName = name === "" || isValidJobName(name);
  return validName
    ? null
    : `${JOB_NAME_DESCRIPTION} Avoid consecutive dots. Maximum ${MAX_JOB_NAME_LENGTH} characters.`;
}
