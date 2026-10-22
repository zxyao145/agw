import assert from "node:assert/strict";
import test, { afterEach, before } from "node:test";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
let environment: DomEnvironment;
let THEME_TOKEN_NAMES: typeof import("./extension-host-provider").THEME_TOKEN_NAMES;
let readTheme: typeof import("./extension-host-provider").readTheme;
let AppearanceProvider: typeof import("../appearance/appearance-provider").AppearanceProvider;

before(async () => {
  const { setupDomEnvironment } = await import("@agw/test-harness");
  environment = await setupDomEnvironment();
  ({ THEME_TOKEN_NAMES, readTheme } = await import("./extension-host-provider"));
  ({ AppearanceProvider } = await import("../appearance/appearance-provider"));
});
afterEach(() => environment.cleanup());

test("the theme offered to extensions includes the host's monospace font variable", () => {
  assert.ok(THEME_TOKEN_NAMES.includes("--font-mono"));
});

test("the theme offered to extensions includes the font size variables", () => {
  for (const name of ["--agw-font-size-base", "--agw-font-size-min"] as const) {
    assert.ok(THEME_TOKEN_NAMES.includes(name), name);
  }
});

test("the theme offered to extensions excludes the document font size variable", () => {
  assert.equal(
    (THEME_TOKEN_NAMES as readonly string[]).includes("--agw-font-size-document"),
    false,
  );
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
