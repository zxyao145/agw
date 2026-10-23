"use client";

import * as React from "react";

import type { FontSizeSettings } from "@desktop/shared/contracts";
import { resolveAppearance } from "./resolve-appearance";

/**
 * 把字号设置写到 `<html>` 的 inline style。用 `useLayoutEffect`，使 extension host 在下一帧读取到新值。
 * Writes the font size settings to the inline style of `<html>`. `useLayoutEffect` lets the extension host see the new
 * values on the next frame.
 */
export function AppearanceProvider({
  appearance,
  children,
}: {
  appearance: FontSizeSettings;
  children: React.ReactNode;
}) {
  const { baseFontSize, chatFontSize } = appearance;
  React.useLayoutEffect(() => {
    const style = document.documentElement.style;
    const properties = resolveAppearance({ baseFontSize, chatFontSize });
    style.fontSize = "var(--agw-font-size-base)";
    for (const [name, value] of Object.entries(properties)) style.setProperty(name, value);
    return () => {
      style.removeProperty("font-size");
      for (const name of Object.keys(properties)) style.removeProperty(name);
    };
  }, [baseFontSize, chatFontSize]);

  return <>{children}</>;
}
