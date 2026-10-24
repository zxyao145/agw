import { APPEARANCE_MODES, DEFAULT_THEME_ID, extensionThemeId } from "@desktop/shared/appearance";
import type { AppearanceMode, ExtensionTheme } from "@desktop/shared/contracts";
import type { ExtensionState } from "../extensions/extension-host";

export const DEFAULT_THEME_TITLE = "Default";

/** 主题提供工作区背景时 `<html>` 上的属性。The `<html>` attribute set while the theme provides a workspace backdrop. */
export const WORKSPACE_BACKDROP_ATTRIBUTE = "data-workspace-backdrop";

/** 主题变量中是否有工作区背景（值不为 none）。Whether the theme variables carry a workspace backdrop other than none. */
export function hasWorkspaceBackdrop(tokens: Readonly<Record<string, string>>): boolean {
  const backdrop = tokens["--agw-workspace-backdrop"];
  return backdrop !== undefined && backdrop !== "none";
}

/** Theme 下拉框中的一个 extension 主题。An extension theme in the Theme select. */
export type AvailableTheme = {
  /** `"{extensionId}:{themeId}"`。 */
  id: string;
  title: string;
  extensionName: string;
  /** 下拉框显示的名称：同名时附上 extension 名称。The select label; duplicated titles carry the extension name. */
  label: string;
  theme: ExtensionTheme;
};

/** 启用且合法的 extension 提供的主题。The themes of enabled, valid extensions. */
export function listAvailableThemes(extensions: readonly ExtensionState[]): AvailableTheme[] {
  const themes = extensions.flatMap(({ installed }) =>
    installed.enabled && installed.validation === "valid"
      ? installed.themes.map((theme) => ({
          id: extensionThemeId(installed.id, theme.id),
          title: theme.title,
          extensionName: installed.manifest?.name ?? installed.id,
          theme,
        }))
      : [],
  );
  const titles = [DEFAULT_THEME_TITLE, ...themes.map((theme) => theme.title)];
  return themes.map((theme) => ({
    ...theme,
    label:
      titles.filter((title) => title === theme.title).length > 1
        ? `${theme.title} (${theme.extensionName})`
        : theme.title,
  }));
}

/**
 * manifest 中声明的主题名称，包括停用或不合法的 extension：主题 id → title。
 * Theme titles declared in manifests, disabled or invalid extensions included: theme id → title.
 */
export function declaredThemeTitles(extensions: readonly ExtensionState[]): Map<string, string> {
  return new Map(
    extensions.flatMap(({ installed }) =>
      (installed.manifest?.contributes?.themes ?? []).map((theme): [string, string] => [
        extensionThemeId(installed.id, theme.id),
        theme.title,
      ]),
    ),
  );
}

/** 主题在浅色或深色下的变量：`light`、`dark` 覆盖 `common`。The variables for light or dark; the side overrides `common`. */
export function themeTokens(theme: ExtensionTheme, kind: "light" | "dark"): Record<string, string> {
  return { ...theme.common, ...theme[kind] };
}

/** next-themes 保存的值转换为 Mode，没有值时为 system。Converts the next-themes value; none means system. */
export function toAppearanceMode(value: string | undefined): AppearanceMode {
  return APPEARANCE_MODES.find((mode) => mode === value) ?? "system";
}

export function isDefaultTheme(themeId: string): boolean {
  return themeId === DEFAULT_THEME_ID;
}
