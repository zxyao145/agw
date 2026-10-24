import assert from "node:assert/strict";
import test, { before } from "node:test";

import type { ThemeInfo } from "../api";

let applyTheme: typeof import("./theme").applyTheme;

before(async () => {
  const { setupDomEnvironment } = await import("@agw/test-harness");
  await setupDomEnvironment();
  ({ applyTheme } = await import("./theme"));
});

const theme = (kind: "light" | "dark"): ThemeInfo => ({
  kind,
  tokens: { "--agw-font-size-base": "18px" },
});

test("applyTheme links the typography stylesheet once across repeated calls", () => {
  applyTheme(theme("light"), document);
  applyTheme(theme("dark"), document);

  const links = document.head.querySelectorAll("link[data-agw-typography]");
  assert.equal(links.length, 1);
  assert.equal(links[0]?.getAttribute("href"), "/__agw/typography.css");
  assert.equal(links[0]?.getAttribute("rel"), "stylesheet");
});

test("applyTheme writes the token values and the dark class", () => {
  applyTheme(theme("dark"), document);

  assert.equal(document.documentElement.style.getPropertyValue("--agw-font-size-base"), "18px");
  assert.ok(document.documentElement.classList.contains("dark"));
  assert.equal(document.documentElement.style.colorScheme, "dark");
});
