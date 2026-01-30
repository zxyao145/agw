import { DirectoryMode, EnvVar, PermissionMode } from "../types";

export type ClaudeSettingsStorageValues = {
  workingDirectory?: string;
  gitAddress?: string;
  directoryMode?: DirectoryMode;
  apiKey?: string;
  apiBaseUrl?: string;
  permissionMode?: PermissionMode | string;
  envVars?: EnvVar[];
  workingDirHistory?: string[];
  gitAddressHistory?: string[];
};

const STORAGE_KEY = "claudecode_settings";

const parseJson = <T>(value: string | null, label: string): T | undefined => {
  if (!value) {
    return undefined;
  }
  try {
    return JSON.parse(value) as T;
  } catch (error) {
    console.error(`Failed to parse ${label}:`, error);
    return undefined;
  }
};

const canUseStorage = () =>
  typeof window !== "undefined" && typeof window.localStorage !== "undefined";

const readStoredSettings = (): ClaudeSettingsStorageValues => {
  const stored = parseJson<unknown>(
    localStorage.getItem(STORAGE_KEY),
    "settings",
  );
  if (!stored) {
    return {};
  }
  return stored as ClaudeSettingsStorageValues;
};

const mergeSettings = (
  current: ClaudeSettingsStorageValues,
  updates: ClaudeSettingsStorageValues,
): ClaudeSettingsStorageValues => {
  const next: ClaudeSettingsStorageValues = { ...current };

  const res: ClaudeSettingsStorageValues = {
    ...next,
    ...pickDefined(updates),

    // 特殊字段合并
    envVars: mergeEnvVars(next.envVars, updates.envVars),
    workingDirHistory: mergeUnique(
      next.workingDirHistory,
      updates.workingDirHistory,
    ),
    gitAddressHistory: mergeUnique(
      next.gitAddressHistory,
      updates.gitAddressHistory,
    ),
  };

  return res;
};

function pickDefined<T extends object>(obj: T): Partial<T> {
  const result: Partial<T> = {};

  for (const key in obj) {
    const value = obj[key];
    if (value !== undefined) {
      result[key] = value;
    }
  }

  return result;
}
function mergeUnique(a?: string[], b?: string[]): string[] | undefined {
  if (!a && !b) return undefined;
  if (!a) return b;
  if (!b) return a;

  return Array.from(new Set([...a, ...b]));
}
function mergeEnvVars(a?: EnvVar[], b?: EnvVar[]): EnvVar[] | undefined {
  if (!a && !b) return undefined;
  if (!a) return b;
  if (!b) return a;

  const map = new Map<string, EnvVar>();

  for (const env of a) {
    map.set(env.key, env);
  }

  for (const env of b) {
    map.set(env.key, env); // override wins
  }

  return Array.from(map.values());
}

export const claudeSettingsStorage = {
  get(): ClaudeSettingsStorageValues {
    if (!canUseStorage()) {
      return {};
    }

    try {
      const stored = readStoredSettings();
      return stored;
    } catch (error) {
      console.warn("Storage access failed while reading settings.", error);
      return {};
    }
  },
  set(values: ClaudeSettingsStorageValues): void {
    if (!canUseStorage()) {
      return;
    }

    try {
      const current = readStoredSettings();
      const next = mergeSettings(current, values);
      localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
    } catch (error) {
      console.warn("Storage access failed while writing settings.", error);
    }
  },
};
