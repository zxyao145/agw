"use client";

import { CodeViewAppearanceProvider } from "@agw/code-view";
import { useTheme } from "next-themes";

/**
 * Files 的 Monaco 查看器跟随 Web 的浅色或深色：以 next-themes 解析出的主题作为外观版本。
 * Files' Monaco viewers follow Web's light or dark mode, with next-themes' resolved theme as the appearance revision.
 */
export function WebCodeViewAppearance({ children }: { children: React.ReactNode }) {
  const { resolvedTheme } = useTheme();
  return (
    <CodeViewAppearanceProvider revision={resolvedTheme}>{children}</CodeViewAppearanceProvider>
  );
}
