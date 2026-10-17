import type { ExtensionSettings } from "./extension";
import type { ServerProfile } from "./server-profile";

export type PackageFlavor = "full" | "client";
export type CloseBehavior = "minimize-to-tray" | "quit-desktop";

export type DesktopSettings = {
  schemaVersion: 1;
  packageFlavor: PackageFlavor;
  closeBehavior: CloseBehavior;
  profiles: ServerProfile[];
  activeServerId: string;
  projectTabsByServer: Record<string, string[]>;
  /** 只通过 extension 管理通道修改。Changed only through the extension management channels. */
  extensions: Record<string, ExtensionSettings>;
};

/** Only fields changed by the caller; tab entries merge per Server. */
export type DesktopSettingsUpdate = Partial<
  Pick<DesktopSettings, "closeBehavior" | "profiles" | "activeServerId" | "projectTabsByServer">
>;
