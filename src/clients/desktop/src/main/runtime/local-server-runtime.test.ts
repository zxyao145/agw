import assert from "node:assert/strict";
import test from "node:test";

import { parseLocalServerRuntime } from "./local-server-runtime";

test("parseLocalServerRuntime accepts the Server runtime descriptor", () => {
  assert.deepEqual(
    parseLocalServerRuntime(
      JSON.stringify({
        schemaVersion: 1,
        pid: 42,
        baseUrl: "http://127.0.0.1:43123",
        port: 43123,
        serverVersion: "1.0.0",
        apiMajorVersion: 1,
        startedAt: "2026-07-17T00:00:00Z",
      }),
    ),
    {
      schemaVersion: 1,
      pid: 42,
      baseUrl: "http://127.0.0.1:43123",
      port: 43123,
      serverVersion: "1.0.0",
      apiMajorVersion: 1,
      startedAt: "2026-07-17T00:00:00Z",
    },
  );
});

test("parseLocalServerRuntime rejects non-loopback and incompatible descriptors", () => {
  assert.equal(
    parseLocalServerRuntime(
      JSON.stringify({
        schemaVersion: 1,
        pid: 42,
        baseUrl: "http://example.com:30815",
        port: 30815,
        serverVersion: "1.0.0",
        apiMajorVersion: 1,
        startedAt: "2026-07-17T00:00:00Z",
      }),
    ),
    null,
  );
  assert.equal(parseLocalServerRuntime("{}"), null);
});
