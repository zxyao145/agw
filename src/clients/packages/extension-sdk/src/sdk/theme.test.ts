import assert from "node:assert/strict";
import test, { before } from "node:test";

import type { ThemeChangedNotification, ThemeInfo } from "../api";

// 模块加载时建立 DOM 环境，关闭 window 的 after 钩子属于整个文件。The DOM environment is set up at module load, so the
// after hook that closes the window belongs to the file.
const environmentReady = import("@agw/test-harness").then(({ setupDomEnvironment }) =>
  setupDomEnvironment(),
);

let theme: typeof import("./theme");

before(async () => {
  await environmentReady;
  theme = await import("./theme");
});

const OPTIONS = { extensionId: "sample", defaultsText: ":root { --spacing: 0.25rem; }" };

const jsonTheme = (kind: "light" | "dark", tokens: Record<string, string>): ThemeInfo => ({
  id: "soft-theme:soft",
  title: "Soft",
  kind,
  tokens,
});

test("applyTheme links the typography stylesheet once and marks the extension document", () => {
  theme.applyTheme({ theme: jsonTheme("light", {}), styleId: null }, document, OPTIONS);
  theme.applyTheme({ theme: jsonTheme("dark", {}), styleId: null }, document, OPTIONS);

  const links = document.head.querySelectorAll("link[data-agw-typography]");
  assert.equal(links.length, 1);
  assert.equal(links[0]?.getAttribute("href"), "/__agw/typography.css");
  assert.equal(document.documentElement.dataset.agwDocument, "extension");
  assert.equal(document.documentElement.dataset.agwExtension, "sample");
});

test("JSON themes write their variables inline, replace the previous ones and set the dark class", () => {
  const root = document.documentElement;
  theme.applyTheme(
    {
      theme: jsonTheme("dark", { "--background": "#212121", "--agw-font-size-base": "18px" }),
      styleId: null,
    },
    document,
    OPTIONS,
  );
  assert.equal(root.style.getPropertyValue("--background"), "#212121");
  assert.equal(root.style.getPropertyValue("--agw-font-size-base"), "18px");
  assert.ok(root.classList.contains("dark"));
  assert.equal(root.style.colorScheme, "dark");

  theme.applyTheme(
    { theme: jsonTheme("light", { "--agw-font-size-base": "16px" }), styleId: null },
    document,
    OPTIONS,
  );
  assert.equal(root.style.getPropertyValue("--background"), "");
  assert.equal(root.style.getPropertyValue("--agw-font-size-base"), "16px");
  assert.equal(root.classList.contains("dark"), false);
  // 其他代码写入的 inline 属性保留。Inline properties written by other code stay.
  root.style.setProperty("--page-own", "1");
  theme.applyTheme({ theme: jsonTheme("light", {}), styleId: null }, document, OPTIONS);
  assert.equal(root.style.getPropertyValue("--page-own"), "1");
  root.style.removeProperty("--page-own");
});

const CSS_TEXT = ":root.dark { --background: #111; }";

test("theme changes merge into the complete state before public events", () => {
  const css = { theme: { ...jsonTheme("dark", {}), cssText: CSS_TEXT }, styleId: "a" };
  const change = (style: ThemeChangedNotification["style"]): ThemeChangedNotification => ({
    theme: jsonTheme("light", { "--background": "#fff" }),
    style,
  });

  // unchanged 保留已有文本；公开状态仍然完整。unchanged keeps the text; the public state stays complete.
  assert.deepEqual(theme.mergeThemeChange(css, change({ operation: "unchanged", styleId: "a" })), {
    theme: { ...jsonTheme("light", { "--background": "#fff" }), cssText: CSS_TEXT },
    styleId: "a",
  });
  assert.deepEqual(
    theme.mergeThemeChange(
      css,
      change({ operation: "replace", styleId: "b", cssText: ":root {}" }),
    ),
    {
      theme: { ...jsonTheme("light", { "--background": "#fff" }), cssText: ":root {}" },
      styleId: "b",
    },
  );
  const cleared = theme.mergeThemeChange(css, change({ operation: "clear", styleId: null }));
  assert.deepEqual(cleared, {
    theme: jsonTheme("light", { "--background": "#fff" }),
    styleId: null,
  });
  assert.equal("cssText" in cleared.theme, false);

  // 字段省略不表示保留样式：没有 CSS 的状态收到 unchanged(null) 仍然没有 CSS。An omitted field keeps nothing: a state
  // without CSS stays without CSS on unchanged(null).
  const plain = { theme: jsonTheme("dark", {}), styleId: null };
  assert.equal(
    "cssText" in
      theme.mergeThemeChange(plain, change({ operation: "unchanged", styleId: null })).theme,
    false,
  );
});

test("an unchanged operation with another style id is a protocol error", () => {
  const css = { theme: { ...jsonTheme("dark", {}), cssText: CSS_TEXT }, styleId: "a" };
  assert.throws(
    () =>
      theme.mergeThemeChange(css, {
        theme: jsonTheme("dark", {}),
        style: { operation: "unchanged", styleId: "b" },
      }),
    { code: "ERR_PROTOCOL" },
  );
  assert.throws(
    () =>
      theme.mergeThemeChange(
        { theme: jsonTheme("dark", {}), styleId: null },
        { theme: jsonTheme("dark", {}), style: { operation: "unchanged", styleId: "a" } },
      ),
    { code: "ERR_PROTOCOL" },
  );
});
