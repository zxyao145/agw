export { AppShell } from "./components/app-shell";
export { DesktopChatPage } from "./desktop-chat-page";
export { DesktopConnectionGate } from "./desktop-connection-gate";
export { DesktopWorkspaceBoundary, DesktopWorkspaceErrorState } from "./desktop-workspace-boundary";
export { DesktopSettingsPage } from "./desktop-settings-page";
export { DesktopExtensionsPage } from "./desktop-extensions-page";
export { DesktopExtensionSettingsPage } from "./desktop-extension-settings-page";
export { ExtensionHostProvider } from "./extensions/extension-host-provider";
export {
  classifyDesktopConnection,
  getActiveServerProfile,
  getEffectiveActiveServerProfile,
  type DesktopConnectionStatus,
  type DesktopRuntimeState,
  type DesktopSettings,
  type ServerInfo,
  type ServerProfile,
} from "./runtime-model";
export { DesktopRuntimeProvider, useDesktopRuntime } from "./runtime-provider";
