import assert from "node:assert/strict";
import test, { afterEach, before } from "node:test";

import { THEME_TOKEN_KINDS, THEME_TOKEN_NAMES } from "@desktop/shared/theme-tokens";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
let environment: DomEnvironment;
let readTheme: typeof import("./extension-host-provider").readTheme;
let AppearanceProvider: typeof import("../appearance/appearance-provider").AppearanceProvider;

before(async () => {
  const { setupDomEnvironment } = await import("@agw/test-harness");
  environment = await setupDomEnvironment();
  ({ readTheme } = await import("./extension-host-provider"));
  ({ AppearanceProvider } = await import("../appearance/appearance-provider"));
});
afterEach(() => environment.cleanup());

test("the theme offered to extensions includes every variable a theme may set", () => {
  for (const name of THEME_TOKEN_KINDS.keys()) assert.ok(THEME_TOKEN_NAMES.includes(name), name);
  for (const name of [
    "--background",
    "--agw-sidebar-muted-foreground",
    "--agw-code-block-button-hover",
    "--agw-image-toolbar-shadow",
    "--agw-agentflow-edge-switch-default",
    "--color-blue-950",
    "--color-black",
    "--radius",
    "--agw-font-sans",
  ]) {
    assert.ok(THEME_TOKEN_NAMES.includes(name), name);
  }
});

test("the theme offered to extensions includes the computed radius and font variables", () => {
  for (const name of [
    "--radius-sm",
    "--radius-md",
    "--radius-lg",
    "--radius-xl",
    "--font-sans",
    "--font-mono",
  ]) {
    assert.ok(THEME_TOKEN_NAMES.includes(name), name);
  }
});

test("the theme offered to extensions includes the font size variables", () => {
  for (const name of ["--agw-font-size-base", "--agw-font-size-min"] as const) {
    assert.ok(THEME_TOKEN_NAMES.includes(name), name);
  }
});

test("the theme offered to extensions excludes the document font size variable", () => {
  assert.equal(THEME_TOKEN_NAMES.includes("--agw-font-size-document"), false);
});

test("readTheme reads the font size variables written by AppearanceProvider", () => {
  environment.render(
    environment.React.createElement(AppearanceProvider, {
      appearance: { baseFontSize: 20, chatFontSize: null },
      children: null,
    }),
  );

  const { tokens } = readTheme("light").theme;

  assert.equal(tokens["--agw-font-size-base"], "20px");
  // 最小字号来自 typography.css 的默认值，AppearanceProvider 不写入。
  // The minimum size comes from the typography.css default; AppearanceProvider does not write it.
  assert.equal("--agw-font-size-min" in tokens, false);
});
