import type { DesktopSettings, PackageFlavor } from "./settings-store";
import type { LocalServerRuntime } from "./local-server-runtime";

export type DesktopRuntimeState = {
  isDesktop: true;
  platform: NodeJS.Platform;
  packageFlavor: PackageFlavor;
  settings: DesktopSettings;
  activeToken: string | null;
  localServerRuntime: LocalServerRuntime | null;
};

export type UninstallRequest = {
  deleteServerData: boolean;
};

export type UninstallResult = {
  manualActionRequired: boolean;
  message: string;
};

export type AgwDesktopBridge = {
  getRuntimeState(): Promise<DesktopRuntimeState>;
  saveSettings(settings: DesktopSettings): Promise<DesktopRuntimeState>;
  saveToken(profileId: string, token: string): Promise<void>;
  deleteToken(profileId: string): Promise<void>;
  provisionLocalToken(): Promise<string>;
  openSetup(baseUrl: string): Promise<void>;
  setActiveTaskCount(count: number): Promise<void>;
  prepareUninstall(request: UninstallRequest): Promise<UninstallResult>;
  showWindow(): Promise<void>;
  quitDesktop(): Promise<void>;
};
