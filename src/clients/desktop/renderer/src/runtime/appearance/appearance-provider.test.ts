import assert from "node:assert/strict";
import test, { afterEach, before } from "node:test";

import type { FontSizeSettings } from "@desktop/shared/contracts";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
let environment: DomEnvironment;
let AppearanceProvider: typeof import("./appearance-provider").AppearanceProvider;

before(async () => {
  const { setupDomEnvironment } = await import("@agw/test-harness");
  environment = await setupDomEnvironment();
  ({ AppearanceProvider } = await import("./appearance-provider"));
});
afterEach(() => environment.cleanup());

const root = () => document.documentElement.style;
const providerElement = (appearance: FontSizeSettings) =>
  environment.React.createElement(AppearanceProvider, { appearance, children: null });

test("AppearanceProvider writes the font size properties to the document root", () => {
  environment.render(providerElement({ baseFontSize: 20, chatFontSize: null }));

  assert.equal(root().fontSize, "var(--agw-font-size-base)");
  assert.equal(root().getPropertyValue("--agw-font-size-chat"), "17.5px");
  assert.equal(root().getPropertyValue("--agw-font-size-document"), "");
  assert.equal(root().getPropertyValue("--agw-font-size-min"), "");
});

test("AppearanceProvider floors the seven theme text sizes below 1rem at the minimum font size", () => {
  environment.render(providerElement({ baseFontSize: 16, chatFontSize: null }));

  const expected = {
    "--text-3xs": "0.5625rem",
    "--text-2xs": "0.625rem",
    "--text-xs": "0.75rem",
    "--text-11": "0.6875rem",
    "--text-sm": "0.875rem",
    "--text-13": "0.8125rem",
    "--text-15": "0.9375rem",
  };
  for (const [name, size] of Object.entries(expected)) {
    assert.equal(root().getPropertyValue(name), `max(var(--agw-font-size-min), ${size})`, name);
  }
});

test("AppearanceProvider updates the properties when the appearance changes", () => {
  const view = environment.render(providerElement({ baseFontSize: 20, chatFontSize: null }));

  view.rerender(providerElement({ baseFontSize: 16, chatFontSize: 15 }));

  assert.equal(root().getPropertyValue("--agw-font-size-base"), "16px");
  assert.equal(root().getPropertyValue("--agw-font-size-chat"), "15px");
});

test("AppearanceProvider removes every property it set when it unmounts", () => {
  const view = environment.render(providerElement({ baseFontSize: 20, chatFontSize: null }));

  view.unmount();

  assert.equal(root().length, 0);
  assert.equal(root().fontSize, "");
});
