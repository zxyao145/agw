import { contextBridge, ipcRenderer } from "electron";

import type {
  AgwDesktopBridge,
  DesktopRuntimeState,
  DesktopSettings,
  UninstallRequest,
  UninstallResult,
} from "@agw/desktop-contracts";

const bridge: AgwDesktopBridge = {
  getRuntimeState: () =>
    ipcRenderer.invoke("agw:get-runtime-state") as Promise<DesktopRuntimeState>,
  saveSettings: (settings: DesktopSettings) =>
    ipcRenderer.invoke("agw:save-settings", settings) as Promise<DesktopRuntimeState>,
  saveToken: (profileId: string, token: string) =>
    ipcRenderer.invoke("agw:save-token", profileId, token) as Promise<void>,
  deleteToken: (profileId: string) =>
    ipcRenderer.invoke("agw:delete-token", profileId) as Promise<void>,
  provisionLocalToken: () => ipcRenderer.invoke("agw:provision-local-token") as Promise<string>,
  openSetup: (baseUrl: string) => ipcRenderer.invoke("agw:open-setup", baseUrl) as Promise<void>,
  setActiveTaskCount: (count: number) =>
    ipcRenderer.invoke("agw:set-active-task-count", count) as Promise<void>,
  prepareUninstall: (request: UninstallRequest) =>
    ipcRenderer.invoke("agw:prepare-uninstall", request) as Promise<UninstallResult>,
  showWindow: () => ipcRenderer.invoke("agw:show-window") as Promise<void>,
  quitDesktop: () => ipcRenderer.invoke("agw:quit-desktop") as Promise<void>,
};

contextBridge.exposeInMainWorld("agwDesktop", bridge);
