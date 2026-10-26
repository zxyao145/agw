// 宿主外观到 Monaco 主题与字体的转换（设计文档第 2 节）。Converts the host appearance into the Monaco theme and font
// (design section 2).
import { monaco } from "./setup";

export const LIGHT_THEME = "agw-light";
export const DARK_THEME = "agw-dark";

// 选区用宿主的强调蓝色加透明度，文字保持可读。The selection is the host's accent blue made translucent, so text stays
// readable.
const SELECTION_ALPHA = "40";

/**
 * 编辑器使用的字体与字号（px）；行高取 Monaco 按字号计算的默认值。
 * The editor font and size in px; the line height is Monaco's default for the size.
 */
export interface HostFont {
  fontFamily: string;
  fontSize: number;
}

export interface HostAppearance {
  theme: typeof LIGHT_THEME | typeof DARK_THEME;
  colors: Record<string, string>;
  font: HostFont;
}

let canvasContext: CanvasRenderingContext2D | null = null;

/**
 * 把颜色值（含 oklch、color-mix 等写法）换成 Monaco 主题要求的 `#rrggbbaa`：在画布上画一个像素再读回。
 * Converts a color value (oklch, color-mix and other notations included) to the `#rrggbbaa` form Monaco themes need,
 * by painting one pixel on a canvas and reading it back.
 */
function toThemeColor(name: string, value: string): string {
  if (!CSS.supports("color", value)) {
    throw new Error(`The host variable ${name} is not a color: "${value}".`);
  }
  if (canvasContext === null) {
    const context = document.createElement("canvas").getContext("2d", { willReadFrequently: true });
    if (!context) throw new Error("A 2D canvas is not available.");
    canvasContext = context;
  }
  canvasContext.clearRect(0, 0, 1, 1);
  canvasContext.fillStyle = value;
  canvasContext.fillRect(0, 0, 1, 1);
  const channels = Array.from(canvasContext.getImageData(0, 0, 1, 1).data);
  return `#${channels.map((channel) => channel.toString(16).padStart(2, "0")).join("")}`;
}

function pixels(name: string, value: string): number {
  if (!value.endsWith("px")) throw new Error(`The host ${name} is not in px: "${value}".`);
  return Number.parseFloat(value);
}

/**
 * 读取 `host` 上计算后的宿主颜色变量、字体与字号。字号来自 CSS 中的 `--agw-font-size-code`。
 * Reads the computed host color variables, font and size on `host`. The size comes from `--agw-font-size-code` in
 * the CSS.
 */
export function readHostAppearance(host: HTMLElement): HostAppearance {
  const style = getComputedStyle(host);
  const color = (name: string) => {
    const value = style.getPropertyValue(name).trim();
    if (value === "") throw new Error(`The host has no color variable ${name}.`);
    return toThemeColor(name, value);
  };
  return {
    theme: document.documentElement.classList.contains("dark") ? DARK_THEME : LIGHT_THEME,
    colors: {
      "editor.background": color("--background"),
      "editor.foreground": color("--foreground"),
      "editorLineNumber.foreground": color("--muted-foreground"),
      "editor.selectionBackground": `${color("--sidebar-primary").slice(0, 7)}${SELECTION_ALPHA}`,
    },
    font: {
      fontFamily: style.fontFamily,
      fontSize: pixels("font-size", style.fontSize),
    },
  };
}

/**
 * 定义并切换到宿主主题，返回编辑器的字体选项。Monaco 的主题对整个页面生效。
 * Defines and switches to the host theme and returns the editor font options. Monaco's theme applies to the whole
 * page.
 */
export function applyHostAppearance(appearance: HostAppearance): HostFont {
  monaco.editor.defineTheme(appearance.theme, {
    base: appearance.theme === DARK_THEME ? "vs-dark" : "vs",
    inherit: true,
    rules: [],
    colors: appearance.colors,
  });
  monaco.editor.setTheme(appearance.theme);
  return appearance.font;
}
