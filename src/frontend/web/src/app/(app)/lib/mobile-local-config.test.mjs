import assert from "node:assert/strict";
import test from "node:test";

import { copyMobileLocalConfigToClipboard } from "./mobile-local-config.ts";

test("copyMobileLocalConfigToClipboard posts serverDomain and copies response payload", async () => {
  const copiedValues = [];
  const requests = [];

  await copyMobileLocalConfigToClipboard({
    serverDomain: "https://api.example.com",
    request: async (input, init) => {
      requests.push({ input, init });
      return {
        ok: true,
        status: 200,
        statusText: "OK",
        json: async () => ({
          code: 0,
          title: "OK",
          data: { payload: "encoded-config" },
        }),
      };
    },
    writeText: async (value) => {
      copiedValues.push(value);
    },
  });

  assert.deepEqual(copiedValues, ["encoded-config"]);
  assert.equal(requests[0]?.input, "/api/setup/mobile-local-config");
  assert.equal(requests[0]?.init.method, "POST");
  assert.equal(requests[0]?.init.headers["content-type"], "application/json");
  assert.equal(requests[0]?.init.body, JSON.stringify({ serverDomain: "https://api.example.com" }));
});

test("copyMobileLocalConfigToClipboard rejects invalid response payload", async () => {
  await assert.rejects(
    () =>
      copyMobileLocalConfigToClipboard({
        serverDomain: "https://api.example.com",
        request: async () => ({
          ok: true,
          status: 200,
          statusText: "OK",
          json: async () => ({ code: 0, title: "OK", data: {} }),
        }),
        writeText: async () => {},
      }),
    /payload/i,
  );
});
