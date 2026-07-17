import assert from "node:assert/strict";
import test from "node:test";

import { resolveRendererFile } from "./renderer-path";

test("renderer path resolves Next export pages and assets", () => {
  assert.equal(resolveRendererFile("/renderer", "/"), "/renderer/index.html");
  assert.equal(resolveRendererFile("/renderer", "/settings/"), "/renderer/settings/index.html");
  assert.equal(
    resolveRendererFile("/renderer", "/_next/static/app.js"),
    "/renderer/_next/static/app.js",
  );
});

test("renderer path rejects directory traversal", () => {
  assert.throws(() => resolveRendererFile("/renderer", "/../secrets.json"), /outside renderer/);
  assert.throws(() => resolveRendererFile("/renderer", "/%2e%2e/secrets.json"), /outside renderer/);
});
