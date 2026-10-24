"use client";

import * as React from "react";
import { usePathname, useRouter } from "next/navigation";

import type { ThemeInfo } from "@agw/extension-sdk";
import { THEME_TOKEN_NAMES } from "@desktop/shared/theme-tokens";
import { useDesktopTheme } from "../appearance/theme-provider";
import { HostConfirmationDialog } from "../components/workspace/host-confirmation-dialog";
import { useDesktopRuntime } from "../runtime-provider";
import { normalizeBaseUrl } from "./context-service";
import { type ExtensionRuntime, getExtensionRuntime } from "./extension-runtime";

const ExtensionHostContext = React.createContext<ExtensionRuntime | null>(null);

/**
 * 读取推送给 extension 的主题：正在使用的主题的 id 与名称，以及变量的计算值，其中的 `var()` 已经替换。
 * Reads the theme pushed to extensions: the id and title of the theme in use, and the variables' computed values with
 * `var()` substituted.
 */
export function readTheme(
  kind: "light" | "dark",
  applied: { id: string; title: string },
): { theme: ThemeInfo; background: string } {
  const style = getComputedStyle(document.documentElement);
  const tokens = Object.fromEntries(
    THEME_TOKEN_NAMES.map((name) => [name, style.getPropertyValue(name).trim()]).filter(
      ([, value]) => value,
    ),
  );
  const background =
    getComputedStyle(document.body).backgroundColor || (kind === "dark" ? "#0b0c0f" : "#ffffff");
  return { theme: { id: applied.id, title: applied.title, kind, tokens }, background };
}

function isChatPath(pathname: string | null): boolean {
  return (pathname ?? "").replace(/\/+$/u, "") === "/desktop/chat";
}

/** 在 `DesktopConnectionGate` 外层提供 extension 宿主。Provides the extension host outside `DesktopConnectionGate`. */
export function ExtensionHostProvider({ children }: { children: React.ReactNode }) {
  const desktop = useDesktopRuntime();
  // 静态导出预渲染阶段没有 window，不创建单例。Prerendering has no window, so no singleton is created.
  const value = React.useMemo(
    () => (typeof window === "undefined" ? null : getExtensionRuntime()),
    [],
  );
  const host = value?.host ?? null;
  const router = useRouter();
  const pathname = usePathname();
  const pathnameRef = React.useRef(pathname);
  pathnameRef.current = pathname;
  const { kind, appliedTokens, appliedTheme } = useDesktopTheme();

  React.useEffect(() => {
    if (!value) return;
    value.tracker.start();
    void value.host
      .start()
      .catch((error: unknown) => console.error("Unable to start extensions.", error));
  }, [value]);

  React.useEffect(() => {
    if (!host) return;
    host.layout.setNavigateToChat(() => {
      if (!isChatPath(pathnameRef.current)) router.push("/desktop/chat/");
    });
    return () => host.layout.setNavigateToChat(null);
  }, [host, router]);

  const runtimeState = desktop.runtimeState;
  React.useEffect(() => {
    if (!host || !runtimeState) return;
    host.context.setApp({
      version: runtimeState.appVersion,
      platform: runtimeState.platform as "darwin" | "win32" | "linux",
      architecture: runtimeState.architecture,
    });
  }, [host, runtimeState]);

  const profile = desktop.activeProfile;
  const token = runtimeState?.activeToken ?? null;
  React.useEffect(() => {
    if (!host) return;
    host.context.setConnection(
      profile ? { profileId: profile.id, baseUrl: normalizeBaseUrl(profile.baseUrl), token } : null,
      profile ? { id: profile.id, name: profile.name, kind: profile.kind } : null,
    );
  }, [host, profile, token]);

  const authUser = desktop.authUser;
  React.useEffect(() => {
    const key = host?.context.connectionKey;
    if (!host || !key) return;
    host.context.setUser(
      key,
      authUser
        ? {
            id: authUser.userId,
            displayName: authUser.displayName,
            loginProvider: authUser.loginProvider,
            isAdmin: authUser.isAdmin,
          }
        : null,
    );
  }, [host, authUser, profile, token]);

  // 主题、实际深浅色与字号设置变化时推送主题。Theme, light/dark and font size changes push the theme.
  const baseFontSize = runtimeState?.settings.appearance.baseFontSize;
  const chatFontSize = runtimeState?.settings.appearance.chatFontSize;
  React.useEffect(() => {
    if (!host || !kind) return;
    // 等 class 与主题变量写到 document 后再读取变量。Read variables after the class and theme variables are applied.
    const frame = requestAnimationFrame(() => {
      const { theme, background } = readTheme(kind, appliedTheme);
      host.setTheme(theme, background);
    });
    return () => cancelAnimationFrame(frame);
  }, [host, kind, appliedTokens, appliedTheme, baseFontSize, chatFontSize]);

  return (
    <ExtensionHostContext value={value}>
      {children}
      {host ? <HostConfirmationDialog host={host} /> : null}
    </ExtensionHostContext>
  );
}

/** 宿主与遮挡跟踪器；预渲染阶段为 null。The host and overlay tracker; null while prerendering. */
export function useExtensionRuntime(): ExtensionRuntime | null {
  return React.useContext(ExtensionHostContext);
}
