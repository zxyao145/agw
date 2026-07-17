export type ServerProfile = {
  id: string;
  kind: "local" | "remote";
  name: string;
  baseUrl: string;
  apiMajorVersion: 1;
  allowInsecureHttp: boolean;
};

export type DesktopSettings = {
  schemaVersion: 1;
  packageFlavor: "full" | "client";
  closeBehavior: "minimize-to-tray" | "quit-desktop";
  profiles: ServerProfile[];
  activeServerId: string;
  projectTabsByServer: Record<string, string[]>;
};

export type DesktopRuntimeState = {
  isDesktop: true;
  platform:
    | "aix"
    | "android"
    | "darwin"
    | "freebsd"
    | "haiku"
    | "linux"
    | "openbsd"
    | "sunos"
    | "win32"
    | "cygwin"
    | "netbsd";
  packageFlavor: "full" | "client";
  settings: DesktopSettings;
  activeToken: string | null;
  localServerRuntime: {
    schemaVersion: 1;
    pid: number;
    baseUrl: string;
    port: number;
    serverVersion: string;
    apiMajorVersion: 1;
    startedAt: string;
  } | null;
};

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
