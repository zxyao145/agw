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
  /** 用户卸载的内置 extension id，它们不再出现。Ids of bundled extensions the user uninstalled; they no longer appear. */
  removedBundledExtensions: string[];
  /**
   * 文件类型（`*.md`、`dockerfile`）→ 用户选择的默认查看器 id（`<extensionId>:<viewerId>` 或 `"source"`）。
   * File type (`*.md`, `dockerfile`) → the user's default viewer id (`<extensionId>:<viewerId>` or `"source"`).
   */
  fileViewerDefaults: Record<string, string>;
};

/** Only fields changed by the caller; tab entries merge per Server. */
export type DesktopSettingsUpdate = Partial<
  Pick<DesktopSettings, "closeBehavior" | "profiles" | "activeServerId" | "projectTabsByServer">
>;
