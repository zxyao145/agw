"use client";

import * as React from "react";
import { useTheme } from "next-themes";
import { usePathname, useRouter } from "next/navigation";
import { toast } from "sonner";

import type { ThemeInfo } from "@agw/extension-sdk";
import { HostConfirmationDialog } from "../components/workspace/host-confirmation-dialog";
import { useDesktopRuntime } from "../runtime-provider";
import { normalizeBaseUrl } from "./context-service";
import { ExtensionHost } from "./extension-host";
import { HostOverlayTracker } from "./host-overlay-tracker";

/** 提供给 extension 的 tokens.css 颜色与圆角变量。tokens.css color and radius variables offered to extensions. */
export const THEME_TOKEN_NAMES = [
  "--background",
  "--foreground",
  "--card",
  "--card-foreground",
  "--popover",
  "--popover-foreground",
  "--primary",
  "--primary-foreground",
  "--secondary",
  "--secondary-foreground",
  "--muted",
  "--muted-foreground",
  "--accent",
  "--accent-foreground",
  "--destructive",
  "--border",
  "--input",
  "--ring",
  "--chart-1",
  "--chart-2",
  "--chart-3",
  "--chart-4",
  "--chart-5",
  "--sidebar",
  "--sidebar-foreground",
  "--sidebar-primary",
  "--sidebar-primary-foreground",
  "--sidebar-accent",
  "--sidebar-accent-foreground",
  "--sidebar-border",
  "--sidebar-ring",
  "--radius",
  "--radius-sm",
  "--radius-md",
  "--radius-lg",
  "--radius-xl",
] as const;

type ExtensionRuntime = { host: ExtensionHost; tracker: HostOverlayTracker };

const ExtensionHostContext = React.createContext<ExtensionRuntime | null>(null);

// 模块级单例：生命周期不绑定 effect 的清理，宿主外壳卸载不会关闭 frame。
// Module-level singletons: their lifetime is not bound to effect cleanup, so shell unmounts never close frames.
let runtime: ExtensionRuntime | null = null;

function getExtensionRuntime(): ExtensionRuntime {
  if (runtime) return runtime;
  const host = new ExtensionHost({
    // 普通浏览器中没有 bridge，布局照常工作且不加载 extension。Without a bridge the layout works and no extension loads.
    bridge: window.agwDesktop ?? null,
    app: { version: "", platform: "darwin", architecture: "" },
    notify: (extensionName, notification) => {
      const message = `${extensionName}: ${notification.message}`;
      if (notification.type === "error") toast.error(message);
      else if (notification.type === "success") toast.success(message);
      else toast(message);
    },
  });
  const tracker = new HostOverlayTracker(document, (frameIds) => host.setObscuredFrames(frameIds));
  runtime = { host, tracker };
  return runtime;
}

function readTheme(kind: "light" | "dark"): { theme: ThemeInfo; background: string } {
  const style = getComputedStyle(document.documentElement);
  const tokens = Object.fromEntries(
    THEME_TOKEN_NAMES.map((name) => [name, style.getPropertyValue(name).trim()]).filter(
      ([, value]) => value,
    ),
  );
  const background =
    getComputedStyle(document.body).backgroundColor || (kind === "dark" ? "#0b0c0f" : "#ffffff");
  return { theme: { kind, tokens }, background };
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
  const { resolvedTheme } = useTheme();

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

  React.useEffect(() => {
    if (!host || !resolvedTheme) return;
    // 等 next-themes 把 class 写到 document 后再读取变量。Read variables after next-themes applies the class.
    const frame = requestAnimationFrame(() => {
      const { theme, background } = readTheme(resolvedTheme === "dark" ? "dark" : "light");
      host.setTheme(theme, background);
    });
    return () => cancelAnimationFrame(frame);
  }, [host, resolvedTheme]);

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
