"use client";

import * as React from "react";
import { useTheme } from "next-themes";
import { toast } from "sonner";

import { DEFAULT_THEME_ID } from "@desktop/shared/appearance";
import type { AppearanceMode, AppearanceSettings } from "@desktop/shared/contracts";
import type { ExtensionHost, ExtensionHostSnapshot } from "../extensions/extension-host";
import {
  type AvailableTheme,
  DEFAULT_THEME_TITLE,
  declaredThemeTitles,
  hasWorkspaceBackdrop,
  isDefaultTheme,
  listAvailableThemes,
  themeTokens,
  toAppearanceMode,
  WORKSPACE_BACKDROP_ATTRIBUTE,
} from "./resolve-theme";

export type DesktopThemeState = {
  /** 有效 Mode：设置中的 `mode`，缺失时为 next-themes 保存的值。The effective mode: the stored one, else next-themes'. */
  mode: AppearanceMode;
  /** 实际的浅色或深色；next-themes 还没有确定时为 null。The actual light or dark; null until next-themes resolves it. */
  kind: "light" | "dark" | null;
  /** 设置中的外观，`mode` 为有效 Mode；设置加载之前为 null。The stored appearance with the effective mode; null until loaded. */
  appearance: AppearanceSettings | null;
  /** 可用的 extension 主题。The available extension themes. */
  themes: readonly AvailableTheme[];
  /** 写到 `<html>` 上的主题变量；Default 为空。The theme variables written to `<html>`; empty for Default. */
  appliedTokens: Readonly<Record<string, string>>;
  /** 正在使用的主题：选中且可用的主题，否则为 Default。The theme in use: the selected available one, else Default. */
  appliedTheme: { id: string; title: string };
  /** 保存外观，然后让 next-themes 使用其中的 Mode。Saves the appearance, then applies its mode through next-themes. */
  saveAppearance(next: AppearanceSettings): Promise<void>;
  /** 保存 Mode，然后调用 next-themes 的 setTheme。Saves the mode, then calls next-themes' setTheme. */
  setMode(mode: AppearanceMode): Promise<void>;
};

const DesktopThemeContext = React.createContext<DesktopThemeState | null>(null);

// 与 next-themes 的 disableTransitionOnChange 相同的规则。The same rule as next-themes' disableTransitionOnChange.
const DISABLE_TRANSITIONS_CSS =
  "*,*::before,*::after{-webkit-transition:none!important;-moz-transition:none!important;-o-transition:none!important;-ms-transition:none!important;transition:none!important}";

/**
 * 禁用过渡时执行修改：读取计算样式使新值立即生效，再在下一个任务中恢复过渡。
 * Runs the change with transitions disabled; reading the computed style applies the new values at once, and
 * transitions come back in the next task.
 */
function withoutTransitions(change: () => void): void {
  const style = document.createElement("style");
  style.append(document.createTextNode(DISABLE_TRANSITIONS_CSS));
  document.head.append(style);
  change();
  void window.getComputedStyle(document.body).opacity;
  setTimeout(() => style.remove(), 1);
}

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : "Unable to save appearance settings";
}

const noSubscription = () => () => undefined;
const noSnapshot = () => null;

/**
 * 按选中的主题与 Mode 设置 `<html>` 的主题变量与 dark class，并迁移、校正设置中的 Mode 与主题。
 * Applies the selected theme's variables and the dark class to `<html>`, and migrates and corrects the stored mode and
 * theme.
 */
export function ThemeProvider({
  appearance,
  save,
  host,
  children,
}: {
  /** 设置中的外观；设置加载之前为 null。The stored appearance; null until the settings load. */
  appearance: AppearanceSettings | null;
  save(next: AppearanceSettings): Promise<void>;
  /** 提供 extension 状态；预渲染阶段为 null。Provides the extension state; null while prerendering. */
  host: ExtensionHost | null;
  children: React.ReactNode;
}) {
  const { theme: storedTheme, resolvedTheme, setTheme } = useTheme();
  const snapshot = React.useSyncExternalStore<ExtensionHostSnapshot | null>(
    host?.subscribe ?? noSubscription,
    host?.getSnapshot ?? noSnapshot,
    noSnapshot,
  );
  const nextThemesMode = toAppearanceMode(storedTheme);
  const mode = appearance?.mode ?? nextThemesMode;
  const kind = resolvedTheme === "dark" ? "dark" : resolvedTheme === "light" ? "light" : null;
  const current = React.useMemo(
    () => (appearance ? { ...appearance, mode } : null),
    [appearance, mode],
  );

  const extensions = snapshot?.extensions;
  const themes = React.useMemo(() => listAvailableThemes(extensions ?? []), [extensions]);
  const selected = themes.find((theme) => theme.id === appearance?.theme) ?? null;
  // 内容相同的变量保持同一个对象，extension 状态的其他变化不会重写变量。
  // Equal variables keep one object, so other extension state changes do not rewrite them.
  const tokensKey = JSON.stringify(selected && kind ? themeTokens(selected.theme, kind) : {});
  const appliedTokens = React.useMemo<Readonly<Record<string, string>>>(
    () => JSON.parse(tokensKey),
    [tokensKey],
  );
  const appliedId = selected?.id ?? DEFAULT_THEME_ID;
  const appliedTitle = selected?.title ?? DEFAULT_THEME_TITLE;
  const appliedTheme = React.useMemo(
    () => ({ id: appliedId, title: appliedTitle }),
    [appliedId, appliedTitle],
  );

  // 变量与工作区背景属性在同一次修改中写入。The variables and the workspace backdrop attribute change together.
  const writtenRef = React.useRef<readonly string[]>([]);
  React.useLayoutEffect(() => {
    const names = Object.keys(appliedTokens);
    if (names.length === 0 && writtenRef.current.length === 0) return;
    const root = document.documentElement;
    withoutTransitions(() => {
      for (const name of writtenRef.current) root.style.removeProperty(name);
      for (const [name, value] of Object.entries(appliedTokens))
        root.style.setProperty(name, value);
      root.toggleAttribute(WORKSPACE_BACKDROP_ATTRIBUTE, hasWorkspaceBackdrop(appliedTokens));
    });
    writtenRef.current = names;
  }, [appliedTokens]);
  React.useLayoutEffect(
    () => () => {
      const root = document.documentElement;
      for (const name of writtenRef.current) root.style.removeProperty(name);
      root.removeAttribute(WORKSPACE_BACKDROP_ATTRIBUTE);
      writtenRef.current = [];
    },
    [],
  );

  // settings.json 保存 Mode；next-themes 的值与它不同时以设置为准。
  // settings.json holds the mode; a different next-themes value follows the settings.
  const storedMode = appearance?.mode;
  React.useEffect(() => {
    if (storedMode !== undefined && storedMode !== storedTheme) setTheme(storedMode);
  }, [storedMode, storedTheme, setTheme]);

  // 旧版本设置没有 mode：保存 next-themes 的值，保留用户原来的选择。
  // Older settings have no mode: save the next-themes value to keep the user's choice.
  const migrationRef = React.useRef(false);
  React.useEffect(() => {
    if (!appearance || appearance.mode !== undefined || migrationRef.current) return;
    migrationRef.current = true;
    save({ ...appearance, mode: nextThemesMode }).catch((error: unknown) =>
      toast.error(describeError(error)),
    );
  }, [appearance, nextThemesMode, save]);

  const saveAppearance = React.useCallback(
    async (next: AppearanceSettings) => {
      await save(next);
      if (next.mode !== undefined) setTheme(next.mode);
    },
    [save, setTheme],
  );

  const setMode = React.useCallback(
    async (next: AppearanceMode) => {
      if (current) await saveAppearance({ ...current, mode: next });
      else setTheme(next);
    },
    [current, saveAppearance, setTheme],
  );

  // 已删除的 extension 不在状态中，记住见过的主题名称。Deleted extensions leave the state, so remember seen titles.
  const titlesRef = React.useRef(new Map<string, string>());
  React.useEffect(() => {
    for (const [id, title] of declaredThemeTitles(extensions ?? []))
      titlesRef.current.set(id, title);
  }, [extensions]);

  // 收到第一份 extension 状态之后，选中的主题不可用时改回 Default。
  // Once the first extension state arrives, an unavailable selected theme falls back to Default.
  const started = snapshot?.started ?? false;
  const resetRef = React.useRef<string | null>(null);
  React.useEffect(() => {
    if (!started || !current) return;
    if (isDefaultTheme(current.theme) || themes.some((theme) => theme.id === current.theme)) {
      resetRef.current = null;
      return;
    }
    if (resetRef.current === current.theme) return;
    resetRef.current = current.theme;
    toast(
      `${titlesRef.current.get(current.theme) ?? current.theme} is no longer available. Using Default.`,
    );
    saveAppearance({ ...current, theme: DEFAULT_THEME_ID }).catch((error: unknown) =>
      toast.error(describeError(error)),
    );
  }, [started, current, themes, saveAppearance]);

  const value = React.useMemo<DesktopThemeState>(
    () => ({
      mode,
      kind,
      appearance: current,
      themes,
      appliedTokens,
      appliedTheme,
      saveAppearance,
      setMode,
    }),
    [mode, kind, current, themes, appliedTokens, appliedTheme, saveAppearance, setMode],
  );
  return <DesktopThemeContext value={value}>{children}</DesktopThemeContext>;
}

export function useDesktopTheme(): DesktopThemeState {
  const value = React.useContext(DesktopThemeContext);
  if (!value) throw new Error("useDesktopTheme must be used within ThemeProvider.");
  return value;
}
