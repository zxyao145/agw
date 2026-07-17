export { AppShell } from "./components/app-shell";
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
