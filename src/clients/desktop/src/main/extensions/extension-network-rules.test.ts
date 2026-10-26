import assert from "node:assert/strict";
import test from "node:test";

import { parseHostPermissions } from "../../shared/extension-host-permissions";
import {
  isConnectAllowed,
  isNavigationAllowed,
  isProxyHttpRequestAllowed,
  isRequestAllowed,
  isServiceWorkerScriptRequest,
  isWebPermissionAllowed,
  parseResolveProxyResult,
  serviceWorkerRegistrationOutcome,
} from "./extension-network-rules";

const permissions = parseHostPermissions([
  "https://api.example.com",
  "http://plain.example.com",
  "wss://*.example.net",
  "https://turn.example.com:3478",
  "http://127.0.0.1:*",
  "https://*.wild.example.org:8443",
]);

test("CONNECT targets match by host and port, the wildcard port, or the scheme's default port", () => {
  assert.equal(isConnectAllowed(permissions, "api.example.com", 443), true);
  assert.equal(isConnectAllowed(permissions, "api.example.com", 80), false);
  assert.equal(isConnectAllowed(permissions, "plain.example.com", 80), true);
  assert.equal(isConnectAllowed(permissions, "turn.example.com", 3478), true);
  assert.equal(isConnectAllowed(permissions, "turn.example.com", 443), false);
  assert.equal(isConnectAllowed(permissions, "127.0.0.1", 30816), true);
  assert.equal(isConnectAllowed(permissions, "a.wild.example.org", 8443), true);
  assert.equal(isConnectAllowed(permissions, "wild.example.org", 8443), false);
  assert.equal(isConnectAllowed(permissions, "chat.example.net", 443), true);
  assert.equal(isConnectAllowed(permissions, "deep.chat.example.net", 443), true);
  assert.equal(isConnectAllowed(permissions, "example.net", 443), false);
});

test("loopback addresses are blocked unless declared", () => {
  const none = parseHostPermissions(["https://api.example.com"]);
  assert.equal(isConnectAllowed(none, "127.0.0.1", 30816), false);
  assert.equal(isConnectAllowed(none, "localhost", 30816), false);
  assert.equal(isRequestAllowed("sample", none, "http://localhost:30816/api/files/list"), false);
  assert.equal(isRequestAllowed("sample", none, "http://127.0.0.1:30816/api"), false);
});

test("absolute-URL HTTP requests at the proxy only match http entries", () => {
  assert.equal(isProxyHttpRequestAllowed(permissions, "http://plain.example.com/path"), true);
  assert.equal(isProxyHttpRequestAllowed(permissions, "http://api.example.com/path"), false);
  assert.equal(isProxyHttpRequestAllowed(permissions, "https://api.example.com/path"), false);
  assert.equal(isProxyHttpRequestAllowed(permissions, "http://127.0.0.1:9000/x"), true);
});

test("web requests allow the own origin, local data and approved hosts with matching schemes", () => {
  assert.equal(isRequestAllowed("sample", permissions, "agw-extension://sample/index.html"), true);
  assert.equal(isRequestAllowed("sample", permissions, "agw-extension://other/index.html"), false);
  assert.equal(isRequestAllowed("sample", permissions, "data:text/plain,hi"), true);
  assert.equal(isRequestAllowed("sample", permissions, "blob:agw-extension://sample/1234"), true);
  assert.equal(isRequestAllowed("sample", permissions, "blob:https://evil.example/1234"), false);
  assert.equal(isRequestAllowed("sample", permissions, "https://api.example.com/v1"), true);
  assert.equal(isRequestAllowed("sample", permissions, "wss://api.example.com/socket"), false);
  assert.equal(isRequestAllowed("sample", permissions, "wss://live.example.net/socket"), true);
  assert.equal(isRequestAllowed("sample", permissions, "https://other.example.com/"), false);
  assert.equal(isRequestAllowed("sample", permissions, "file:///etc/passwd"), false);
});

test("web requests allow the DevTools frontend that Inspect opens in the extension session", () => {
  const none = parseHostPermissions([]);
  assert.equal(
    isRequestAllowed("sample", none, "devtools://devtools/bundled/devtools_app.html?remoteBase=x"),
    true,
  );
  assert.equal(
    isRequestAllowed("sample", none, "https://chrome-devtools-frontend.appspot.com/serve_file/x"),
    false,
  );
});

test("resolveProxy results are parsed from the first entry", () => {
  assert.deepEqual(parseResolveProxyResult("DIRECT"), { type: "direct" });
  assert.deepEqual(parseResolveProxyResult("PROXY proxy.local:3128"), {
    type: "http",
    host: "proxy.local",
    port: 3128,
    secure: false,
  });
  assert.deepEqual(parseResolveProxyResult("HTTPS secure.proxy:443; DIRECT"), {
    type: "http",
    host: "secure.proxy",
    port: 443,
    secure: true,
  });
  assert.deepEqual(parseResolveProxyResult("PROXY a:1;PROXY b:2"), {
    type: "http",
    host: "a",
    port: 1,
    secure: false,
  });
  assert.deepEqual(parseResolveProxyResult("SOCKS5 127.0.0.1:1080"), {
    type: "unsupported",
    value: "SOCKS5 127.0.0.1:1080",
  });
});

test("top-level navigation stays on the own origin; nested frames may also use approved http hosts", () => {
  assert.equal(
    isNavigationAllowed("sample", permissions, "agw-extension://sample/other.html", true),
    true,
  );
  assert.equal(isNavigationAllowed("sample", permissions, "https://api.example.com/", true), false);
  assert.equal(isNavigationAllowed("sample", permissions, "https://api.example.com/", false), true);
  assert.equal(isNavigationAllowed("sample", permissions, "https://evil.example/", false), false);
  assert.equal(isNavigationAllowed("sample", permissions, "agw-extension://other/", false), false);
  assert.equal(isNavigationAllowed("sample", permissions, "wss://live.example.net/", false), false);
});

test("web permissions allow fullscreen and approved clipboard access only", () => {
  assert.equal(isWebPermissionAllowed("fullscreen", []), true);
  assert.equal(isWebPermissionAllowed("clipboard-read", []), false);
  assert.equal(isWebPermissionAllowed("clipboard-read", ["clipboard.read"]), true);
  assert.equal(isWebPermissionAllowed("clipboard-sanitized-write", ["clipboard.read"]), false);
  assert.equal(isWebPermissionAllowed("clipboard-sanitized-write", ["clipboard.write"]), true);
  for (const permission of ["media", "geolocation", "notifications", "hid", "openExternal"]) {
    assert.equal(isWebPermissionAllowed(permission, ["clipboard.read", "clipboard.write"]), false);
  }
});

test("Service Worker script requests are recognized regardless of header case", () => {
  assert.equal(isServiceWorkerScriptRequest({ "Service-Worker": "script" }), true);
  assert.equal(isServiceWorkerScriptRequest({ "service-worker": "script" }), true);
  assert.equal(isServiceWorkerScriptRequest({ "SERVICE-WORKER": "Script" }), true);
  assert.equal(isServiceWorkerScriptRequest({ Accept: "text/html" }), false);
  assert.equal(
    isServiceWorkerScriptRequest({ "Service-Worker-Navigation-Preload": "true" }),
    false,
  );
});

test("any Service Worker registration fails the extension and clears its Service Worker storage", () => {
  for (const scope of [
    "agw-extension://sample/",
    "http://127.0.0.1:9000/",
    "https://api.example.com/app/",
  ]) {
    const outcome = serviceWorkerRegistrationOutcome(scope);
    assert.equal(outcome.code, "SERVICE_WORKER_NOT_ALLOWED");
    assert.deepEqual(outcome.storagesToClear, ["serviceworkers"]);
    assert.match(outcome.message, new RegExp(scope.replaceAll(/[./]/gu, "\\$&"), "u"));
  }
});
