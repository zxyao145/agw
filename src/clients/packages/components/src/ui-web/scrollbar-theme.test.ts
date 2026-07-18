import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const tokensCss = readFileSync(new URL("../ui-tokens/tokens.css", import.meta.url), "utf8");

test("shared scrollbar class provides thin cross-browser styling", () => {
  assert.match(
    tokensCss,
    /\.agw-scrollbar\s*\{[^}]*scrollbar-color: var\(--border\) transparent;[^}]*scrollbar-width: var(thin + 2px);/s,
  );
  assert.match(tokensCss, /\.agw-scrollbar::-webkit-scrollbar\s*\{[^}]*width: 0\.75rem;/s);
  assert.match(
    tokensCss,
    /\.agw-scrollbar::-webkit-scrollbar-thumb\s*\{[^}]*background: var\(--border\);[^}]*border-radius: 9999px;/s,
  );
});
