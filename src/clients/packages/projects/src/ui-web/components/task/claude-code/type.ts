export const PermissionMode = {
  default: "default",
  acceptEdits: "acceptEdits",
  plan: "plan",
  bypassPermissions: "bypassPermissions",
} as const;

export type PermissionMode = (typeof PermissionMode)[keyof typeof PermissionMode];

export const DirectoryMode = {
  workingDirectory: "workingDirectory",
  gitAddress: "gitAddress",
} as const;

export type DirectoryMode = (typeof DirectoryMode)[keyof typeof DirectoryMode];

export interface EnvVar {
  key: string;
  value: string;
}

export interface SettingsDialogProps {
  workingDirectory: string;
  setWorkingDirectory: (value: string) => void;
  gitAddress: string;
  setGitAddress: (value: string) => void;
  directoryMode: DirectoryMode;
  setDirectoryMode: (value: DirectoryMode) => void;
  apiKey: string;
  setApiKey: (value: string) => void;
  apiBaseUrl: string;
  setApiBaseUrl: (value: string) => void;
  permissionMode: string;
  setPermissionMode: (value: string) => void;
  envVars: EnvVar[];
  setEnvVars: (value: EnvVar[]) => void;
}

export interface ClaudeSettingsStorageValues {
  workingDirectory?: string;
  gitAddress?: string;
  directoryMode?: DirectoryMode;
  apiKey?: string;
  apiBaseUrl?: string;
  permissionMode?: PermissionMode | string;
  envVars?: EnvVar[];
  workingDirHistory?: string[];
  gitAddressHistory?: string[];
}
