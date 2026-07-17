import type {
  DesktopRuntimeState,
  DesktopSettings,
  ServerProfile,
} from "@desktop/shared/contracts";

export type {
  DesktopRuntimeState,
  DesktopSettings,
  ServerProfile,
} from "@desktop/shared/contracts";

export type ServerInfo = {
  serverVersion: string;
  apiMajorVersion: number;
  initialized: boolean;
};

export type DesktopConnectionStatus =
  | "loading"
  | "ready"
  | "setup-required"
  | "authentication-required"
  | "incompatible"
  | "unreachable";

export function getActiveServerProfile(settings: DesktopSettings): ServerProfile {
  const profile = settings.profiles.find((item) => item.id === settings.activeServerId);
  if (!profile) throw new Error("The active Server profile does not exist.");
  return profile;
}

export function getEffectiveActiveServerProfile(runtime: DesktopRuntimeState): ServerProfile {
  const profile = getActiveServerProfile(runtime.settings);
  return profile.kind === "local" && runtime.localServerRuntime
    ? { ...profile, baseUrl: runtime.localServerRuntime.baseUrl }
    : profile;
}

export function classifyDesktopConnection(
  profile: ServerProfile,
  serverInfo: ServerInfo,
  token: string | null,
): DesktopConnectionStatus {
  if (!serverInfo.initialized) return "setup-required";
  if (serverInfo.apiMajorVersion !== profile.apiMajorVersion) return "incompatible";
  if (!token) return "authentication-required";
  return "ready";
}
