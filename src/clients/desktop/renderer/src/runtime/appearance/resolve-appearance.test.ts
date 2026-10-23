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

test("resolveAppearance does not write the document or minimum font size", () => {
  const properties = resolveAppearance(appearance(16, null));
  assert.equal("--agw-font-size-document" in properties, false);
  assert.equal("--agw-font-size-min" in properties, false);
});

test("resolveAppearance raises the derived chat size to 14px", () => {
  assert.deepEqual(sizes(appearance(14, null)), ["14px", "14px"]);
});

test("resolveAppearance keeps an explicit chat size independent of the base size", () => {
  assert.deepEqual(sizes(appearance(16, 15)), ["16px", "15px"]);
  assert.deepEqual(sizes(appearance(20, 15)), ["20px", "15px"]);
});

test("resolveAppearance derives the chat size from the base size", () => {
  assert.deepEqual(sizes(appearance(20, null)), ["20px", "17.5px"]);
});

test("resolveAppearance floors every theme size below 1rem with the minimum", () => {
  const properties = resolveAppearance(appearance(16, null));
  for (const [token, value] of [
    ["--text-3xs", "0.5625rem"],
    ["--text-2xs", "0.625rem"],
    ["--text-xs", "0.75rem"],
    ["--text-11", "0.6875rem"],
    ["--text-sm", "0.875rem"],
    ["--text-13", "0.8125rem"],
    ["--text-15", "0.9375rem"],
  ]) {
    assert.equal(properties[token], `max(var(--agw-font-size-min), ${value})`);
  }
});
