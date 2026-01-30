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

const STORAGE_KEYS = {
  workingDirectory: "claudecode_workingDir",
  gitAddress: "claudecode_gitAddress",
  directoryMode: "claudecode_directoryMode",
  apiKey: "claudecode_apiKey",
  apiBaseUrl: "claudecode_apiBaseUrl",
  permissionMode: "claudecode_permissionMode",
  envVars: "claudecode_envVars",
  workingDirHistory: "claudecode_workingDirHistory",
  gitAddressHistory: "claudecode_gitAddressHistory",
} as const;

const parseJson = <T,>(value: string | null, label: string): T | undefined => {
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

const canUseStorage = () => typeof window !== "undefined";

export const claudeSettingsStorage = {
  get(): ClaudeSettingsStorageValues {
    if (!canUseStorage()) {
      return {};
    }

    return {
      workingDirectory: localStorage.getItem(STORAGE_KEYS.workingDirectory) ?? undefined,
      gitAddress: localStorage.getItem(STORAGE_KEYS.gitAddress) ?? undefined,
      directoryMode: (localStorage.getItem(STORAGE_KEYS.directoryMode) as DirectoryMode | null) ?? undefined,
      apiKey: localStorage.getItem(STORAGE_KEYS.apiKey) ?? undefined,
      apiBaseUrl: localStorage.getItem(STORAGE_KEYS.apiBaseUrl) ?? undefined,
      permissionMode: localStorage.getItem(STORAGE_KEYS.permissionMode) ?? undefined,
      envVars: parseJson<EnvVar[]>(localStorage.getItem(STORAGE_KEYS.envVars), "env vars"),
      workingDirHistory: parseJson<string[]>(localStorage.getItem(STORAGE_KEYS.workingDirHistory), "working dir history"),
      gitAddressHistory: parseJson<string[]>(localStorage.getItem(STORAGE_KEYS.gitAddressHistory), "git address history"),
    };
  },
  set(values: ClaudeSettingsStorageValues): void {
    if (!canUseStorage()) {
      return;
    }

    if (values.workingDirectory !== undefined) {
      localStorage.setItem(
        STORAGE_KEYS.workingDirectory,
        values.workingDirectory,
      );
    }
    if (values.gitAddress !== undefined) {
      localStorage.setItem(STORAGE_KEYS.gitAddress, values.gitAddress);
    }
    if (values.directoryMode !== undefined) {
      localStorage.setItem(STORAGE_KEYS.directoryMode, values.directoryMode);
    }
    if (values.apiKey !== undefined) {
      localStorage.setItem(STORAGE_KEYS.apiKey, values.apiKey);
    }
    if (values.apiBaseUrl !== undefined) {
      localStorage.setItem(STORAGE_KEYS.apiBaseUrl, values.apiBaseUrl);
    }
    if (values.permissionMode !== undefined) {
      localStorage.setItem(STORAGE_KEYS.permissionMode, values.permissionMode);
    }
    if (values.envVars !== undefined) {
      localStorage.setItem(STORAGE_KEYS.envVars, JSON.stringify(values.envVars));
    }
    if (values.workingDirHistory !== undefined) {
      localStorage.setItem(
        STORAGE_KEYS.workingDirHistory,
        JSON.stringify(values.workingDirHistory),
      );
    }
    if (values.gitAddressHistory !== undefined) {
      localStorage.setItem(
        STORAGE_KEYS.gitAddressHistory,
        JSON.stringify(values.gitAddressHistory),
      );
    }
  },
};
