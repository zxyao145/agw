import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const tokensCss = readFileSync(new URL("../../ui-tokens/tokens.css", import.meta.url), "utf8");

function getThemeBody(pattern: RegExp, name: string): string {
  const match = tokensCss.match(pattern);
  assert.ok(match, `${name} theme should exist`);
  return match[1];
}

test("primary colors use the Shadcn neutral theme", () => {
  const lightTheme = getThemeBody(/\/\* Colors \*\/([\s\S]*?)\n}/, "light");
  const darkTheme = getThemeBody(/\.dark \{([\s\S]*?)\n}/, "dark");

  assert.match(lightTheme, /--primary: oklch\(0\.205 0 0\);/);
  assert.match(lightTheme, /--primary-foreground: oklch\(0\.985 0 0\);/);
  assert.match(lightTheme, /--ring: oklch\(0\.708 0 0\);/);
  assert.match(darkTheme, /--primary: oklch\(0\.922 0 0\);/);
  assert.match(darkTheme, /--primary-foreground: oklch\(0\.205 0 0\);/);
  assert.match(darkTheme, /--ring: oklch\(0\.556 0 0\);/);
});
