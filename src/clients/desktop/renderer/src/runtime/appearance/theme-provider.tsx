"use client";

import * as React from "react";
import { useTheme } from "next-themes";
import { toast } from "sonner";

import { DEFAULT_THEME_ID } from "@desktop/shared/appearance";
import type { AppearanceMode, AppearanceSettings, ThemeStyleText } from "@desktop/shared/contracts";
import type { ExtensionHost, ExtensionHostSnapshot } from "../extensions/extension-host";
import {
  type AvailableTheme,
  DEFAULT_THEME_TITLE,
  declaredThemeTitles,
  isDefaultTheme,
  listAvailableThemes,
  themeTokens,
  toAppearanceMode,
  WORKSPACE_BACKDROP_ATTRIBUTE,
} from "./resolve-theme";
import {
  isWorkspaceBackdrop,
  type PublishedTheme,
  readTheme,
  samePublishedTheme,
} from "./theme-state";

export type DesktopThemeState = {
  /** 有效 Mode：设置中的 `mode`，缺失时为 next-themes 保存的值。The effective mode: the stored one, else next-themes'. */
  mode: AppearanceMode;
  /** 实际的浅色或深色；next-themes 还没有确定时为 null。The actual light or dark; null until next-themes resolves it. */
  kind: "light" | "dark" | null;
  /** 设置中的外观，`mode` 为有效 Mode；设置加载之前为 null。The stored appearance with the effective mode; null until loaded. */
  appearance: AppearanceSettings | null;
  /** 可用的 extension 主题。The available extension themes. */
  themes: readonly AvailableTheme[];
  /** 写到 `<html>` 上的 JSON 主题变量；Default 与 CSS 主题为空。The JSON theme variables written to `<html>`; empty for Default and CSS themes. */
  appliedTokens: Readonly<Record<string, string>>;
  /** 正在使用的 CSS 主题样式文本的标识；JSON 主题与 Default 为 null。The style id of the CSS theme in use; null for JSON themes and Default. */
  appliedStyleId: string | null;
  /** 正在使用的主题：选中且已经应用的主题，否则为 Default。The theme in use: the selected theme once applied, else Default. */
  appliedTheme: { id: string; title: string };
  /** 最近一次读取并发布给 extension 的主题；第一次读取之前为 null。The theme last read and published to extensions; null before the first read. */
  publishedTheme: PublishedTheme | null;
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

/** 正在使用的主题：JSON 变量或 CSS 样式文本。The theme in use: JSON variables or a CSS style text. */
type AppliedTheme = {
  id: string;
  title: string;
  tokens: Readonly<Record<string, string>>;
  style: ThemeStyleText | null;
};

const DEFAULT_APPLIED: AppliedTheme = {
  id: DEFAULT_THEME_ID,
  title: DEFAULT_THEME_TITLE,
  tokens: {},
  style: null,
};

function sameApplied(left: AppliedTheme, right: AppliedTheme): boolean {
  return (
    left.id === right.id &&
    left.title === right.title &&
    left.style?.styleId === right.style?.styleId &&
    JSON.stringify(left.tokens) === JSON.stringify(right.tokens)
  );
}

const noSubscription = () => () => undefined;
const noSnapshot = () => null;

/**
 * 按选中的主题与 Mode 管理 `<html>` 上主题系统拥有的部分：JSON 主题的 inline 变量与 CSS 主题的 constructed stylesheet；
 * 每次变化之后在下一帧读取实际的深浅色、工作区背景与公共变量并发布；迁移、校正设置中的 Mode 与主题。
 * Manages what the theme system owns on `<html>` for the selected theme and mode: JSON theme inline variables and the
 * CSS theme constructed stylesheet; after every change it reads the actual light or dark, the workspace backdrop and
 * the public variables on the next frame and publishes them; and it migrates and corrects the stored mode and theme.
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
  const selectedId = selected?.id ?? null;
  const selectedTitle = selected?.title ?? null;
  // 内容相同的变量保持同一个字符串，extension 状态的其他变化不会重写变量。
  // Equal variables keep one string, so other extension state changes do not rewrite them.
  const jsonTokensKey =
    selected?.theme.format === "json" && kind
      ? JSON.stringify(themeTokens(selected.theme, kind))
      : null;
  const cssStyleId = selected?.theme.format === "css" ? selected.theme.styleId : null;

  // CSS 主题的样式文本：复用当前 styleId 的文本；递增序号使过期的获取结果不能覆盖新的选择。
  // The CSS theme style text: the text of the current styleId is reused, and an increasing sequence keeps stale
  // results from overriding a newer selection.
  const [loadedStyle, setLoadedStyle] = React.useState<{
    themeId: string;
    style: ThemeStyleText;
  } | null>(null);
  const loadedRef = React.useRef(loadedStyle);
  loadedRef.current = loadedStyle;
  const requestRef = React.useRef(0);
  const cssThemeId = cssStyleId === null ? null : selectedId;
  React.useEffect(() => {
    const sequence = ++requestRef.current;
    if (!host || cssThemeId === null || cssStyleId === null) {
      // 切换到 JSON 或 Default 之后释放不再使用的文本。Text no longer in use is released after switching away.
      setLoadedStyle(null);
      return;
    }
    const loaded = loadedRef.current;
    if (loaded?.themeId === cssThemeId && loaded.style.styleId === cssStyleId) return;
    host.getThemeStyle({ themeId: cssThemeId, styleId: cssStyleId }).then(
      (style) => {
        if (sequence === requestRef.current) setLoadedStyle({ themeId: cssThemeId, style });
      },
      (error: unknown) => {
        if (sequence !== requestRef.current) return;
        toast.error(error instanceof Error ? error.message : String(error));
        // 主题在列表读取之后发生变化：重新读取列表，得到新的描述或不可用状态。The theme changed after the list
        // was read, so read the list again for the new descriptor or its unavailability.
        host.rescan().catch((rescanError: unknown) => toast.error(describeError(rescanError)));
      },
    );
  }, [host, cssThemeId, cssStyleId]);

  // 选中的主题准备完成后成为正在使用的主题；获取 CSS 期间继续使用当前主题。
  // The selected theme becomes the theme in use once ready; while its CSS loads the current theme stays.
  const resolved = React.useMemo<AppliedTheme | null>(() => {
    if (selectedId === null || selectedTitle === null) return DEFAULT_APPLIED;
    if (cssStyleId === null) {
      return {
        id: selectedId,
        title: selectedTitle,
        tokens: JSON.parse(jsonTokensKey ?? "{}"),
        style: null,
      };
    }
    return loadedStyle?.themeId === selectedId && loadedStyle.style.styleId === cssStyleId
      ? { id: selectedId, title: selectedTitle, tokens: {}, style: loadedStyle.style }
      : null;
  }, [selectedId, selectedTitle, jsonTokensKey, cssStyleId, loadedStyle]);
  const [applied, setApplied] = React.useState<AppliedTheme>(DEFAULT_APPLIED);
  if (resolved !== null && !sameApplied(resolved, applied)) setApplied(resolved);

  // 下一帧的状态读取，多个触发合并为一次。The next-frame state read; several triggers merge into one read.
  const [publishedTheme, setPublishedTheme] = React.useState<PublishedTheme | null>(null);
  const appliedRef = React.useRef(applied);
  const kindRef = React.useRef(kind);
  const frameRef = React.useRef(0);
  const scheduleRead = React.useCallback(() => {
    if (frameRef.current !== 0) return;
    frameRef.current = requestAnimationFrame(() => {
      frameRef.current = 0;
      const root = document.documentElement;
      const rootKind = root.classList.contains("dark") ? "dark" : "light";
      // 等待 next-themes 完成 class 更新：class 变化会再次调度读取。Wait for next-themes to update the class; the
      // class change schedules another read.
      if (kindRef.current !== rootKind) return;
      const backdrop = getComputedStyle(root).getPropertyValue("--agw-workspace-backdrop");
      root.toggleAttribute(WORKSPACE_BACKDROP_ATTRIBUTE, isWorkspaceBackdrop(backdrop));
      const current = appliedRef.current;
      const { theme, background } = readTheme(rootKind, current);
      const next: PublishedTheme = { theme, background, style: current.style };
      setPublishedTheme((previous) => (samePublishedTheme(previous, next) ? previous : next));
    });
  }, []);
  React.useLayoutEffect(() => {
    kindRef.current = kind;
    scheduleRead();
  }, [kind, scheduleRead]);
  const baseFontSize = appearance?.baseFontSize;
  const chatFontSize = appearance?.chatFontSize;
  React.useEffect(() => scheduleRead(), [baseFontSize, chatFontSize, scheduleRead]);
  React.useEffect(() => {
    const observer = new MutationObserver(scheduleRead);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["class"] });
    document.fonts.addEventListener("loadingdone", scheduleRead);
    return () => {
      observer.disconnect();
      document.fonts.removeEventListener("loadingdone", scheduleRead);
    };
  }, [scheduleRead]);

  // 在一次同步修改中更新主题系统拥有的 inline 变量与样式表，保留其他代码的样式表与属性。
  // Updates the inline variables and stylesheet the theme system owns in one synchronous change, keeping the
  // stylesheets and properties of other code.
  const writtenRef = React.useRef<readonly string[]>([]);
  const sheetRef = React.useRef<{ sheet: CSSStyleSheet; styleId: string } | null>(null);
  React.useLayoutEffect(() => {
    appliedRef.current = applied;
    const names = Object.keys(applied.tokens);
    if (
      names.length > 0 ||
      writtenRef.current.length > 0 ||
      applied.style !== null ||
      sheetRef.current !== null
    ) {
      const root = document.documentElement;
      withoutTransitions(() => {
        for (const name of writtenRef.current) root.style.removeProperty(name);
        for (const [name, value] of Object.entries(applied.tokens))
          root.style.setProperty(name, value);
        if (applied.style) {
          const owned = sheetRef.current ?? { sheet: new CSSStyleSheet(), styleId: "" };
          if (owned.styleId !== applied.style.styleId) {
            owned.sheet.replaceSync(applied.style.cssText);
            owned.styleId = applied.style.styleId;
          }
          sheetRef.current = owned;
          if (!document.adoptedStyleSheets.includes(owned.sheet)) {
            document.adoptedStyleSheets = [...document.adoptedStyleSheets, owned.sheet];
          }
        } else if (sheetRef.current) {
          const { sheet } = sheetRef.current;
          document.adoptedStyleSheets = document.adoptedStyleSheets.filter(
            (item) => item !== sheet,
          );
          sheetRef.current = null;
        }
      });
    }
    writtenRef.current = names;
    scheduleRead();
  }, [applied, scheduleRead]);
  React.useLayoutEffect(
    () => () => {
      cancelAnimationFrame(frameRef.current);
      frameRef.current = 0;
      const root = document.documentElement;
      for (const name of writtenRef.current) root.style.removeProperty(name);
      if (sheetRef.current) {
        const { sheet } = sheetRef.current;
        document.adoptedStyleSheets = document.adoptedStyleSheets.filter((item) => item !== sheet);
        sheetRef.current = null;
      }
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

  const appliedTheme = React.useMemo(
    () => ({ id: applied.id, title: applied.title }),
    [applied.id, applied.title],
  );
  const value = React.useMemo<DesktopThemeState>(
    () => ({
      mode,
      kind,
      appearance: current,
      themes,
      appliedTokens: applied.tokens,
      appliedStyleId: applied.style?.styleId ?? null,
      appliedTheme,
      publishedTheme,
      saveAppearance,
      setMode,
    }),
    [mode, kind, current, themes, applied, appliedTheme, publishedTheme, saveAppearance, setMode],
  );
  return <DesktopThemeContext value={value}>{children}</DesktopThemeContext>;
}

export function useDesktopTheme(): DesktopThemeState {
  const value = React.useContext(DesktopThemeContext);
  if (!value) throw new Error("useDesktopTheme must be used within ThemeProvider.");
  return value;
}
