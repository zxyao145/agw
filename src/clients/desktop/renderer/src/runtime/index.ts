export { AppShell } from "./components/app-shell";
export { DesktopConnectionGate } from "./desktop-connection-gate";
export { DesktopWorkspaceBoundary, DesktopWorkspaceErrorState } from "./desktop-workspace-boundary";
export { DesktopSettingsPage } from "./desktop-settings-page";
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
