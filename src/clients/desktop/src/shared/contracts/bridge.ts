import type { DesktopRuntimeState } from "./runtime";
import type { DesktopSettings } from "./settings";
import type { DesktopUpdateCheckResult } from "./update";

export type UninstallRequest = {
  deleteServerData: boolean;
};

export type UninstallResult = {
  manualActionRequired: boolean;
  message: string;
};

export type TurnNotificationStatus = "completed" | "failed";

export type TurnNotificationRequest = {
  status: TurnNotificationStatus;
};

export type AgwDesktopBridge = {
  getRuntimeState(): Promise<DesktopRuntimeState>;
  checkForUpdates(): Promise<DesktopUpdateCheckResult>;
  saveSettings(settings: DesktopSettings): Promise<DesktopRuntimeState>;
  saveToken(profileId: string, token: string): Promise<void>;
  deleteToken(profileId: string): Promise<void>;
  provisionLocalToken(): Promise<string>;
  openExternal(url: string): Promise<void>;
  openSetup(baseUrl: string): Promise<void>;
  setActiveTaskCount(count: number): Promise<void>;
  prepareUninstall(request: UninstallRequest): Promise<UninstallResult>;
  showWindow(): Promise<void>;
  showTurnNotification(request: TurnNotificationRequest): Promise<void>;
  quitDesktop(): Promise<void>;
};
