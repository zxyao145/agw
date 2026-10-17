import assert from "node:assert/strict";
import test from "node:test";

import { buildExtensionCsp } from "./extension-csp";

function directives(csp: string): Map<string, string> {
  return new Map(
    csp.split("; ").map((directive) => {
      const [name, ...sources] = directive.split(" ");
      return [name, sources.join(" ")];
    }),
  );
}

test("without host permissions only the extension itself is allowed and frames are blocked", () => {
  const csp = directives(buildExtensionCsp([]));
  assert.equal(csp.get("default-src"), "'none'");
  assert.equal(csp.get("script-src"), "'self' 'wasm-unsafe-eval'");
  assert.equal(csp.get("style-src"), "'self' 'unsafe-inline'");
  assert.equal(csp.get("img-src"), "'self' data: blob:");
  assert.equal(csp.get("media-src"), "'self' data: blob:");
  assert.equal(csp.get("font-src"), "'self' data:");
  assert.equal(csp.get("connect-src"), "'self'");
  assert.equal(csp.get("worker-src"), "'self' blob:");
  assert.equal(csp.get("frame-src"), "'none'");
  assert.equal(csp.get("form-action"), "'self'");
  assert.equal(csp.get("base-uri"), "'self'");
  assert.equal(csp.get("object-src"), "'none'");
  assert.equal(csp.get("frame-ancestors"), "'none'");
});

test("approved hosts, including WebSocket origins and wildcard subdomains, extend the source lists", () => {
  const csp = directives(
    buildExtensionCsp([
      "https://api.example.com",
      "wss://*.example.net:8443",
      "http://localhost:*",
    ]),
  );
  const hosts = "https://api.example.com wss://*.example.net:8443 http://localhost:*";
  assert.equal(csp.get("connect-src"), `'self' ${hosts}`);
  assert.equal(csp.get("img-src"), `'self' data: blob: ${hosts}`);
  assert.equal(csp.get("media-src"), `'self' data: blob: ${hosts}`);
  assert.equal(csp.get("frame-src"), hosts);
  assert.equal(csp.get("script-src"), "'self' 'wasm-unsafe-eval'");
  assert.equal(csp.get("frame-ancestors"), "'none'");
});
