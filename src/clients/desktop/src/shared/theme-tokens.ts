/** 主题变量值的类型；`image` 只接受 CSS 渐变。The kind of value a theme variable holds; `image` takes CSS gradients only. */
export type ThemeTokenKind = "color" | "length" | "font-family" | "image";

const INTERFACE_COLOR_TOKENS = [
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
  "--agw-sidebar-muted-foreground",
  "--agw-prose-link",
];

const CHAT_COLOR_TOKENS = [
  "--agw-user-message",
  "--agw-user-message-foreground",
  "--agw-code-block",
  "--agw-code-block-foreground",
  "--agw-code-block-muted",
  "--agw-code-block-border",
  "--agw-code-block-button",
  "--agw-code-block-button-hover",
  "--agw-prose-code-background",
];

const IMAGE_PREVIEW_COLOR_TOKENS = [
  "--agw-image-backdrop",
  "--agw-image-toolbar",
  "--agw-image-toolbar-foreground",
  "--agw-image-toolbar-border",
  "--agw-image-toolbar-hover",
  "--agw-image-toolbar-focus",
  "--agw-image-toolbar-shadow",
];

const HOST_COLOR_TOKENS = [
  "--agw-status-running",
  "--agw-server-online-halo",
  "--agw-agentflow-edge",
  "--agw-agentflow-edge-fan-out",
  "--agw-agentflow-edge-fan-in",
  "--agw-agentflow-edge-switch-case",
  "--agw-agentflow-edge-switch-default",
];

/**
 * 主题可以设置的 Tailwind 基础色阶：名称 → 级别。
 * The Tailwind palette steps a theme may set: name → steps.
 */
const PALETTE_STEPS: Readonly<Record<string, readonly number[]>> = {
  slate: [400],
  zinc: [50, 200, 800, 900],
  red: [50, 100, 200, 400, 700, 800, 900, 950],
  orange: [50, 100, 200, 400, 600, 900, 950],
  amber: [50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 950],
  yellow: [100, 400, 700, 900],
  green: [50, 100, 200, 300, 400, 600, 700, 800, 900, 950],
  emerald: [50, 200, 300, 500, 600, 700, 800],
  teal: [50, 200, 800],
  sky: [50, 200, 600, 800],
  blue: [50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 950],
  indigo: [50, 200, 900],
  violet: [50, 100, 200, 300, 700, 900, 950],
  rose: [50, 200, 300, 500, 800],
};

const PALETTE_COLOR_TOKENS = [
  ...Object.entries(PALETTE_STEPS).flatMap(([name, steps]) =>
    steps.map((step) => `--color-${name}-${step}`),
  ),
  "--color-black",
  "--color-white",
];

/** 主题可以设置的全部变量及其值的类型。Every variable a theme may set, with the kind of its value. */
export const THEME_TOKEN_KINDS: ReadonlyMap<string, ThemeTokenKind> = new Map<
  string,
  ThemeTokenKind
>([
  ...[
    ...INTERFACE_COLOR_TOKENS,
    ...CHAT_COLOR_TOKENS,
    ...IMAGE_PREVIEW_COLOR_TOKENS,
    ...HOST_COLOR_TOKENS,
    ...PALETTE_COLOR_TOKENS,
  ].map((name): [string, ThemeTokenKind] => [name, "color"]),
  ["--radius", "length"],
  ["--agw-font-sans", "font-family"],
  ["--agw-font-mono", "font-family"],
  // Main Workspace 的背景图形，extension 自行决定是否在自己的页面中使用。
  // The Main Workspace backdrop; each extension decides whether its own pages use it.
  ["--agw-workspace-backdrop", "image"],
]);

/**
 * 推送给 extension 的变量：主题可以设置的全部变量，以及由它们计算的圆角、字体变量与字号变量。
 * Variables pushed to extensions: every variable a theme may set, plus the radius and font variables computed from
 * them and the font size variables.
 */
export const THEME_TOKEN_NAMES: readonly string[] = [
  ...THEME_TOKEN_KINDS.keys(),
  "--radius-sm",
  "--radius-md",
  "--radius-lg",
  "--radius-xl",
  "--font-sans",
  "--font-mono",
  "--agw-font-size-base",
];
