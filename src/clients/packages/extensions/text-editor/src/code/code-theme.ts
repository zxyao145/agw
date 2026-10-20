import type { ThemeInfo } from "@agw/extension-sdk";

import { monaco } from "./monaco";

const context = document.createElement("canvas").getContext("2d", { willReadFrequently: true });

/**
 * 把宿主颜色变量的值换成 Monaco 主题要求的 `#rrggbbaa`；颜色由浏览器在画布上解析，oklch 等写法也能转换。
 * Converts a host color variable's value to the `#rrggbbaa` form Monaco themes need; the browser parses the color on
 * a canvas, so notations such as oklch convert too.
 */
function hostColor(theme: ThemeInfo, name: string): string {
  const value = theme.tokens[name];
  if (value === undefined || !CSS.supports("color", value)) {
    throw new Error(`The host theme has no color ${name}.`);
  }
  if (!context) throw new Error("A 2D canvas is not available.");
  context.clearRect(0, 0, 1, 1);
  context.fillStyle = value;
  context.fillRect(0, 0, 1, 1);
  const channels = Array.from(context.getImageData(0, 0, 1, 1).data);
  return `#${channels.map((channel) => channel.toString(16).padStart(2, "0")).join("")}`;
}

/**
 * 按宿主主题定义 `agw-light`（基于 `vs`）或 `agw-dark`（基于 `vs-dark`）并切换到它；主题变化时再次调用。
 * Defines `agw-light` (based on `vs`) or `agw-dark` (based on `vs-dark`) from the host theme and switches to it; call
 * again when the theme changes.
 */
export function applyCodeTheme(theme: ThemeInfo): void {
  const dark = theme.kind === "dark";
  const name = dark ? "agw-dark" : "agw-light";
  monaco.editor.defineTheme(name, {
    base: dark ? "vs-dark" : "vs",
    inherit: true,
    rules: [],
    colors: {
      "editor.background": hostColor(theme, "--background"),
      "editor.foreground": hostColor(theme, "--foreground"),
      "editorLineNumber.foreground": hostColor(theme, "--muted-foreground"),
      // 选区用宿主的强调蓝色加透明度，文字保持可读。
      // The selection is the host's accent blue made translucent, so text stays readable.
      "editor.selectionBackground": `${hostColor(theme, "--sidebar-primary").slice(0, 7)}40`,
    },
  });
  monaco.editor.setTheme(name);
}
