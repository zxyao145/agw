"use client";

import * as React from "react";
import { usePathname, useRouter } from "next/navigation";

import { useDesktopTheme } from "../appearance/theme-provider";
import { useDesktopRuntime } from "../runtime-provider";
import { normalizeBaseUrl } from "./context-service";
import { ExtensionImagePreview } from "./extension-image-preview";
import { type ExtensionRuntime, getExtensionRuntime } from "./extension-runtime";

const ExtensionHostContext = React.createContext<ExtensionRuntime | null>(null);

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
  const { publishedTheme } = useDesktopTheme();

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

  // ThemeProvider 在实际的 dark class 与样式更新之后读取并发布主题，这里推送给 extension 并测量视图区域。
  // ThemeProvider reads and publishes the theme after the actual dark class and styles update; push it to extensions
  // here and measure the view areas.
  // CSS 主题可以改变占位区域的位置与可见性，启用期间连续采样。A CSS theme can move and hide view slots, so sampling runs
  // continuously while one is active.
  React.useEffect(() => {
    if (!value || !publishedTheme) return;
    value.host.setTheme(
      { theme: publishedTheme.theme, style: publishedTheme.style },
      publishedTheme.background,
    );
    value.layout.setContinuous(publishedTheme.style !== null);
    value.layout.schedule();
  }, [value, publishedTheme]);
  React.useEffect(() => () => value?.layout.setContinuous(false), [value]);

  return (
    <ExtensionHostContext value={value}>
      {children}
      {value ? <ExtensionImagePreview host={value.host} /> : null}
    </ExtensionHostContext>
  );
}

/** 宿主与遮挡跟踪器；预渲染阶段为 null。The host and overlay tracker; null while prerendering. */
export function useExtensionRuntime(): ExtensionRuntime | null {
  return React.useContext(ExtensionHostContext);
}
