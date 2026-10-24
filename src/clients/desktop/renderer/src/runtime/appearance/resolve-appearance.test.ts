import assert from "node:assert/strict";
import test from "node:test";

import type { FontSizeSettings } from "@desktop/shared/contracts";
import { resolveAppearance } from "./resolve-appearance";

const appearance = (baseFontSize: number, chatFontSize: number | null): FontSizeSettings => ({
  baseFontSize,
  chatFontSize,
});

function sizes(value: FontSizeSettings) {
  const properties = resolveAppearance(value);
  return [properties["--agw-font-size-base"], properties["--agw-font-size-chat"]];
}

test("resolveAppearance maps the defaults to px values", () => {
  assert.deepEqual(sizes(appearance(16, null)), ["16px", "14px"]);
});

test("resolveAppearance writes only the base and chat font sizes", () => {
  assert.deepEqual(resolveAppearance(appearance(16, null)), {
    "--agw-font-size-base": "16px",
    "--agw-font-size-chat": "14px",
  });
});

test("resolveAppearance derives the chat size 10.5px from base size 12", () => {
  assert.deepEqual(sizes(appearance(12, null)), ["12px", "10.5px"]);
});

test("resolveAppearance keeps an explicit chat size independent of the base size", () => {
  assert.deepEqual(sizes(appearance(16, 15)), ["16px", "15px"]);
  assert.deepEqual(sizes(appearance(20, 15)), ["20px", "15px"]);
});

test("resolveAppearance derives the chat size from the base size", () => {
  assert.deepEqual(sizes(appearance(20, null)), ["20px", "17.5px"]);
});
