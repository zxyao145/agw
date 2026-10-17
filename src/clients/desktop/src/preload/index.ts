import { contextBridge, ipcRenderer } from "electron";

import type {
  AgwDesktopBridge,
  DesktopRuntimeState,
  DesktopSettingsUpdate,
  DesktopUpdateCheckResult,
  ExtensionEvent,
  ExtensionPortMessage,
  ImageContextMenuRequest,
  TurnNotificationRequest,
  UninstallRequest,
  UninstallResult,
} from "../shared/contracts";

// 主进程交付的 extension 端口转交给宿主页面。Hands extension ports from the main process to the host page.
ipcRenderer.on("agw:extension-port", (event, data: Omit<ExtensionPortMessage, "type">) => {
  const message: ExtensionPortMessage = {
    type: "agw:extension-port",
    extensionId: data.extensionId,
    frameId: data.frameId,
    pageId: data.pageId,
  };
  window.postMessage(message, location.origin, event.ports);
});

const bridge: AgwDesktopBridge = {
  getRuntimeState: () =>
    ipcRenderer.invoke("agw:get-runtime-state") as Promise<DesktopRuntimeState>,
  checkForUpdates: () =>
    ipcRenderer.invoke("agw:check-for-updates") as Promise<DesktopUpdateCheckResult>,
  saveSettings: (settings: DesktopSettingsUpdate) =>
    ipcRenderer.invoke("agw:save-settings", settings) as Promise<DesktopRuntimeState>,
  saveToken: (profileId: string, token: string) =>
    ipcRenderer.invoke("agw:save-token", profileId, token) as Promise<void>,
  deleteToken: (profileId: string) =>
    ipcRenderer.invoke("agw:delete-token", profileId) as Promise<void>,
  provisionLocalToken: () => ipcRenderer.invoke("agw:provision-local-token") as Promise<string>,
  getOidcProviders: (profileId: string) => ipcRenderer.invoke("agw:oidc-providers", profileId),
  loginWithOidc: (profileId: string, providerId: string) =>
    ipcRenderer.invoke("agw:oidc-login", profileId, providerId),
  cancelOidcLogin: (profileId: string) => ipcRenderer.invoke("agw:oidc-cancel", profileId),
  logoutOidc: (profileId: string) => ipcRenderer.invoke("agw:oidc-logout", profileId),
  openExternal: (url: string) => ipcRenderer.invoke("agw:open-external", url) as Promise<void>,
  showImageContextMenu: (request: ImageContextMenuRequest) =>
    ipcRenderer.invoke("agw:image-context-menu", request) as Promise<void>,
  openSetup: (baseUrl: string) => ipcRenderer.invoke("agw:open-setup", baseUrl) as Promise<void>,
  setActiveTaskCount: (count: number) =>
    ipcRenderer.invoke("agw:set-active-task-count", count) as Promise<void>,
  prepareUninstall: (request: UninstallRequest) =>
    ipcRenderer.invoke("agw:prepare-uninstall", request) as Promise<UninstallResult>,
  showWindow: () => ipcRenderer.invoke("agw:show-window") as Promise<void>,
  showTurnNotification: (request: TurnNotificationRequest) =>
    ipcRenderer.invoke("agw:show-turn-notification", request) as Promise<void>,
  quitDesktop: () => ipcRenderer.invoke("agw:quit-desktop") as Promise<void>,

  attachExtensionHost: (hostInstanceId) =>
    ipcRenderer.invoke("agw:attach-extension-host", hostInstanceId),
  listExtensions: () => ipcRenderer.invoke("agw:list-extensions"),
  listExtensionRecoveryNotices: () => ipcRenderer.invoke("agw:list-extension-recovery-notices"),
  prepareExtensionInstall: (request) =>
    ipcRenderer.invoke("agw:prepare-extension-install", request),
  commitExtensionInstall: (installId) =>
    ipcRenderer.invoke("agw:commit-extension-install", installId),
  cancelExtensionInstall: (installId) =>
    ipcRenderer.invoke("agw:cancel-extension-install", installId),
  uninstallExtension: (extensionId, options) =>
    ipcRenderer.invoke("agw:uninstall-extension", extensionId, options),
  setExtensionEnabled: (extensionId, enabled, grants) =>
    ipcRenderer.invoke("agw:set-extension-enabled", extensionId, enabled, grants),
  beginExtensionActivation: (hostInstanceId, extensionId) =>
    ipcRenderer.invoke("agw:begin-extension-activation", hostInstanceId, extensionId),
  endExtensionActivation: (hostInstanceId, extensionId) =>
    ipcRenderer.invoke("agw:end-extension-activation", hostInstanceId, extensionId),
  recordExtensionActivation: (hostInstanceId, extensionId, version) =>
    ipcRenderer.invoke("agw:record-extension-activation", hostInstanceId, extensionId, version),
  releaseExtensionFrameResources: (hostInstanceId, extensionId, frameId) =>
    ipcRenderer.invoke(
      "agw:release-extension-frame-resources",
      hostInstanceId,
      extensionId,
      frameId,
    ),
  releaseExtensionResources: (hostInstanceId, extensionId) =>
    ipcRenderer.invoke("agw:release-extension-resources", hostInstanceId, extensionId),
  resumeExtensions: () => ipcRenderer.invoke("agw:resume-extensions"),
  openExtensionsDirectory: () => ipcRenderer.invoke("agw:open-extensions-directory"),
  openExtensionDataDirectory: (extensionId) =>
    ipcRenderer.invoke("agw:open-extension-data-directory", extensionId),
  cancelExtensionHttpRequest: (requestId) =>
    ipcRenderer.invoke("agw:cancel-extension-http-request", requestId),
  saveExtensionSdkTypes: () => ipcRenderer.invoke("agw:save-extension-sdk-types"),
  createExtensionFrame: (hostInstanceId, request) =>
    ipcRenderer.invoke("agw:create-extension-frame", hostInstanceId, request),
  updateExtensionFrameLayout: (hostInstanceId, updates) =>
    ipcRenderer.invoke("agw:update-extension-frame-layout", hostInstanceId, updates),
  obscureExtensionFrames: (hostInstanceId, frameIds) =>
    ipcRenderer.invoke("agw:obscure-extension-frames", hostInstanceId, frameIds),
  revealExtensionFrames: (hostInstanceId, frameIds) =>
    ipcRenderer.invoke("agw:reveal-extension-frames", hostInstanceId, frameIds),
  setExtensionFrameBackground: (hostInstanceId, color) =>
    ipcRenderer.invoke("agw:set-extension-frame-background", hostInstanceId, color),
  destroyExtensionFrame: (hostInstanceId, frameId) =>
    ipcRenderer.invoke("agw:destroy-extension-frame", hostInstanceId, frameId),
  terminateExtensionFrameProcess: (hostInstanceId, frameId) =>
    ipcRenderer.invoke("agw:terminate-extension-frame-process", hostInstanceId, frameId),
  openExtensionFrameDevTools: (hostInstanceId, frameId) =>
    ipcRenderer.invoke("agw:open-extension-frame-dev-tools", hostInstanceId, frameId),

  extensionFileSystem: (caller, request) =>
    ipcRenderer.invoke("agw:extension-file-system", caller, request),
  extensionWatch: (caller, request) => ipcRenderer.invoke("agw:extension-watch", caller, request),
  extensionUnwatch: (caller, watchId) =>
    ipcRenderer.invoke("agw:extension-unwatch", caller, watchId),
  showExtensionOpenDialog: (caller, options) =>
    ipcRenderer.invoke("agw:extension-open-dialog", caller, options),
  showExtensionSaveDialog: (caller, options) =>
    ipcRenderer.invoke("agw:extension-save-dialog", caller, options),
  revealExtensionPath: (caller, uri) =>
    ipcRenderer.invoke("agw:reveal-extension-path", caller, uri),
  extensionHttpRequest: (caller, request) =>
    ipcRenderer.invoke("agw:extension-http-request", caller, request),

  onExtensionEvent: (listener) => {
    const handler = (_event: Electron.IpcRendererEvent, extensionEvent: ExtensionEvent) =>
      listener(extensionEvent);
    ipcRenderer.on("agw:extension-event", handler);
    return () => ipcRenderer.off("agw:extension-event", handler);
  },
};

contextBridge.exposeInMainWorld("agwDesktop", bridge);
