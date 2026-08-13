import type { DesktopPlatform } from "./server-profile";
import type { DesktopSettings, PackageFlavor } from "./settings";

export type LocalServerRuntime = {
  schemaVersion: 1;
  pid: number;
  baseUrl: string;
  port: number;
  serverVersion: string;
  apiMajorVersion: 1;
  startedAt: string;
};

export type DesktopRuntimeState = {
  isDesktop: true;
  platform: DesktopPlatform;
  architecture: string;
  appVersion: string;
  packageFlavor: PackageFlavor;
  settings: DesktopSettings;
  activeToken: string | null;
  localServerRuntime: LocalServerRuntime | null;
};
