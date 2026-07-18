import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const tokensCss = readFileSync(new URL("../ui-tokens/tokens.css", import.meta.url), "utf8");

test("shared scrollbar class provides thin cross-browser styling", () => {
  assert.match(
    tokensCss,
    /\.agw-scrollbar,[^{]*\{[^}]*scrollbar-color: var\(--border\) transparent;[^}]*scrollbar-width: thin;/s,
  );
  assert.match(
    tokensCss,
    /\.agw-scrollbar::-webkit-scrollbar,[^{]*\{[^}]*height: 0\.75rem;[^}]*width: 0\.75rem;/s,
  );
  assert.match(
    tokensCss,
    /\.agw-scrollbar::-webkit-scrollbar-thumb,[^{]*\{[^}]*background: var\(--border\);[^}]*border-radius: 9999px;/s,
  );
  assert.match(tokensCss, /\.prose[^{]*:where\(pre\)[^{]*::-webkit-scrollbar/);
});
