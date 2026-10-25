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
  environment.render(providerElement({ baseFontSize: 20, chatFontSize: null, codeFontSize: null }));

  assert.equal(root().fontSize, "var(--agw-font-size-base)");
  assert.equal(root().getPropertyValue("--agw-font-size-chat"), "17.5px");
  assert.equal(root().getPropertyValue("--agw-font-size-code"), "17.5px");
  assert.equal(root().getPropertyValue("--agw-font-size-document"), "");
  assert.deepEqual(Array.from(root()).sort(), [
    "--agw-font-size-base",
    "--agw-font-size-chat",
    "--agw-font-size-code",
    "font-size",
  ]);
});

test("AppearanceProvider updates the properties when the appearance changes", () => {
  const view = environment.render(
    providerElement({ baseFontSize: 20, chatFontSize: null, codeFontSize: null }),
  );

  view.rerender(providerElement({ baseFontSize: 16, chatFontSize: 15, codeFontSize: 18 }));

  assert.equal(root().getPropertyValue("--agw-font-size-base"), "16px");
  assert.equal(root().getPropertyValue("--agw-font-size-chat"), "15px");
  assert.equal(root().getPropertyValue("--agw-font-size-code"), "18px");
});

test("AppearanceProvider removes every property it set when it unmounts", () => {
  const view = environment.render(
    providerElement({ baseFontSize: 20, chatFontSize: null, codeFontSize: null }),
  );

  view.unmount();

  assert.equal(root().length, 0);
  assert.equal(root().fontSize, "");
});
