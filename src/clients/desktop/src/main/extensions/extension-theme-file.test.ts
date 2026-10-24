import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import test from "node:test";

import { toValidationErrors } from "../../shared/extension-manifest";
import { THEME_TOKEN_KINDS } from "../../shared/theme-tokens";
import {
  isValidThemeValue,
  MAX_THEME_IMAGE_VALUE_LENGTH,
  MAX_THEME_VALUE_LENGTH,
  themeFileSchema,
} from "./extension-theme-file";

function errors(raw: unknown): [string, string | null][] {
  const result = themeFileSchema.safeParse(raw);
  return result.success
    ? []
    : toValidationErrors(result.error.issues).map((error) => [error.code, error.path]);
}

test("the bundled Soft theme file passes validation", async () => {
  const raw = JSON.parse(
    await readFile(
      resolve(process.cwd(), "..", "packages", "extensions", "soft-theme", "themes", "soft.json"),
      "utf8",
    ),
  );
  assert.deepEqual(errors(raw), []);
});

test("the bundled Warm Minimalism theme file passes validation", async () => {
  const raw = JSON.parse(
    await readFile(
      resolve(
        process.cwd(),
        "..",
        "packages",
        "extensions",
        "warm-minimalism-theme",
        "themes",
        "warm-minimalism.json",
      ),
      "utf8",
    ),
  );
  assert.deepEqual(errors(raw), []);
});

test("unknown variable names are rejected with their paths", () => {
  assert.deepEqual(
    errors({ light: { "--background": "#ffffff", "--bg": "#ffffff", "--font-sans": "Arial" } }),
    [
      ["UNKNOWN_THEME_VARIABLE", "light.--bg"],
      ["UNKNOWN_THEME_VARIABLE", "light.--font-sans"],
    ],
  );
});

test("palette steps outside the allowed set are unknown variables", () => {
  assert.deepEqual(errors({ common: { "--color-red-500": "#ff0000" } }), [
    ["UNKNOWN_THEME_VARIABLE", "common.--color-red-500"],
  ]);
  assert.deepEqual(errors({ common: { "--color-purple-500": "#800080" } }), [
    ["UNKNOWN_THEME_VARIABLE", "common.--color-purple-500"],
  ]);
});

test("values must be non-empty strings of at most 200 characters", () => {
  assert.deepEqual(errors({ dark: { "--background": 0 } }), [
    ["INVALID_FIELD", "dark.--background"],
  ]);
  assert.deepEqual(errors({ dark: { "--background": "" } }), [
    ["INVALID_FIELD", "dark.--background"],
  ]);
  assert.deepEqual(
    errors({ common: { "--agw-font-sans": `"${"a".repeat(MAX_THEME_VALUE_LENGTH)}"` } }),
    [["INVALID_FIELD", "common.--agw-font-sans"]],
  );
});

test("color, length and font values must be valid CSS of their kind", () => {
  assert.deepEqual(
    errors({
      common: { "--radius": "red", "--agw-font-mono": "12px" },
      light: { "--background": "#ggg", "--primary": "12px", "--ring": "red; color: blue" },
    }),
    [
      ["INVALID_THEME_VALUE", "common.--radius"],
      ["INVALID_THEME_VALUE", "common.--agw-font-mono"],
      ["INVALID_THEME_VALUE", "light.--background"],
      ["INVALID_THEME_VALUE", "light.--primary"],
      ["INVALID_THEME_VALUE", "light.--ring"],
    ],
  );
});

test("CSS-wide keywords and variable references are not valid values", () => {
  for (const value of ["inherit", "initial", "unset", "revert", "var(--background)"]) {
    assert.equal(isValidThemeValue("color", value), false, value);
    assert.equal(isValidThemeValue("font-family", value), false, value);
  }
  assert.equal(isValidThemeValue("length", "var(--radius)"), false);
});

test("valid values of every kind are accepted", () => {
  for (const value of ["#0d0d0d", "rgb(13 13 13 / 96%)", "oklch(0.95 0 0)", "transparent", "red"]) {
    assert.equal(isValidThemeValue("color", value), true, value);
  }
  for (const value of ["0.75rem", "0", "6px", "calc(0.5rem + 2px)"]) {
    assert.equal(isValidThemeValue("length", value), true, value);
  }
  assert.equal(isValidThemeValue("length", "10%"), false);
  assert.equal(
    isValidThemeValue("font-family", 'ui-monospace, "SF Mono", Menlo, Consolas, monospace'),
    true,
  );
});

test("gradient images accept none and layers of CSS gradients", () => {
  for (const value of [
    "none",
    "linear-gradient(118deg, rgb(229 225 220 / 80%) 0 14%, transparent 14.2%)",
    "radial-gradient(circle 32vmax at 22% 80%, #fbe3d4 0 98%, transparent 100%), linear-gradient(red, blue)",
    "repeating-linear-gradient(45deg, red 0 10px, blue 10px 20px)",
    "conic-gradient(from 90deg at 50% 50%, red, blue)",
    "radial-gradient(28% 22% at 84% 96%, color-mix(in oklab, red 30%, transparent), transparent 70%)",
  ]) {
    assert.equal(isValidThemeValue("image", value), true, value);
  }
});

test("gradient images reject image files, other image functions and non-gradient values", () => {
  for (const value of [
    'url("backdrop.png")',
    "url(backdrop.png), linear-gradient(red, blue)",
    'image-set("backdrop.png" 1x)',
    "cross-fade(linear-gradient(red, blue), linear-gradient(blue, red))",
    "element(#backdrop)",
    "inherit",
    "var(--agw-workspace-backdrop)",
    "linear-gradient(var(--background), red)",
    "#ffffff",
    "linear-gradient(red, blue) no-repeat",
  ]) {
    assert.equal(isValidThemeValue("image", value), false, value);
  }
  assert.deepEqual(errors({ light: { "--agw-workspace-backdrop": 'url("backdrop.png")' } }), [
    ["INVALID_THEME_VALUE", "light.--agw-workspace-backdrop"],
  ]);
});

test("gradient images may be longer than other values up to their own limit", () => {
  const gradient = (length: number) => {
    const start = "linear-gradient(red";
    return `${start}${", blue".repeat(Math.ceil((length - start.length - 1) / 6))})`;
  };
  const long = gradient(MAX_THEME_VALUE_LENGTH * 2);
  assert.ok(long.length > MAX_THEME_VALUE_LENGTH && long.length <= MAX_THEME_IMAGE_VALUE_LENGTH);
  assert.deepEqual(errors({ light: { "--agw-workspace-backdrop": long } }), []);
  const tooLong = gradient(MAX_THEME_IMAGE_VALUE_LENGTH + 1);
  assert.ok(tooLong.length > MAX_THEME_IMAGE_VALUE_LENGTH);
  assert.deepEqual(errors({ light: { "--agw-workspace-backdrop": tooLong } }), [
    ["INVALID_FIELD", "light.--agw-workspace-backdrop"],
  ]);
});

test("every color variable, including the palette, accepts a color", () => {
  const colors = [...THEME_TOKEN_KINDS].filter(([, kind]) => kind === "color");
  assert.ok(colors.some(([name]) => name === "--color-amber-500"));
  assert.ok(colors.some(([name]) => name === "--agw-agentflow-edge-switch-default"));
  assert.deepEqual(
    errors({ light: Object.fromEntries(colors.map(([name]) => [name, "#123456"])) }),
    [],
  );
});

test("a theme needs at least one variable and no other fields", () => {
  assert.deepEqual(errors({}), [["THEME_EMPTY", null]]);
  assert.deepEqual(errors({ common: {}, light: {}, dark: {} }), [["THEME_EMPTY", null]]);
  assert.deepEqual(errors({ light: { "--background": "#ffffff" }, rules: {} }), [
    ["UNKNOWN_FIELD", "rules"],
  ]);
});
