import type { ExtensionSettings } from "./extension";
import type { ServerProfile } from "./server-profile";

export type PackageFlavor = "full" | "client";
export type CloseBehavior = "minimize-to-tray" | "quit-desktop";

/** 浅色、深色或跟随系统。Light, dark or following the system. */
export type AppearanceMode = "system" | "light" | "dark";

/**
 * 字号设置，单位 px。`null` 表示按基础字号推导。
 * Font size settings in px. `null` derives the size from the base font size.
 */
export type FontSizeSettings = {
  baseFontSize: number;
  chatFontSize: number | null;
  /** Files 中 Source 的字号；null 跟随基础字号。Source's size in Files; null follows the base size. */
  codeFontSize: number | null;
};

/** 外观设置：字号、主题与 Mode。Appearance settings: font sizes, theme and mode. */
export type AppearanceSettings = FontSizeSettings & {
  /** `"default"` 或 `"{extensionId}:{themeId}"`。`"default"` or `"{extensionId}:{themeId}"`. */
  theme: string;
  /**
   * 缺失表示还没有从 next-themes 迁移（旧版本的设置或全新安装），renderer 收到设置后写入。
   * Missing until the renderer migrates it from next-themes (older settings or a fresh install).
   */
  mode?: AppearanceMode;
};

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
  appearance: AppearanceSettings;
};

/** Only fields changed by the caller; tab entries merge per Server. */
export type DesktopSettingsUpdate = Partial<
  Pick<
    DesktopSettings,
    "closeBehavior" | "profiles" | "activeServerId" | "projectTabsByServer" | "appearance"
  >
>;
