"use client";

import type * as React from "react";
import { CodeViewAppearanceProvider } from "@agw/code-view";

import { useDesktopTheme } from "./theme-provider";

/**
 * Files 的 Monaco 查看器跟随宿主外观：发布给 extension 的主题在模式、JSON/CSS 主题、字号与工作区背景变化后重新读取，
 * 以它作为外观版本。
 * Files' Monaco viewers follow the host appearance: the theme published to extensions is read again after mode,
 * JSON/CSS theme, font size and workspace backdrop changes, so it serves as the appearance revision.
 */
export function DesktopCodeViewAppearance({ children }: { children: React.ReactNode }) {
  const { publishedTheme } = useDesktopTheme();
  return (
    <CodeViewAppearanceProvider revision={publishedTheme}>{children}</CodeViewAppearanceProvider>
  );
}
