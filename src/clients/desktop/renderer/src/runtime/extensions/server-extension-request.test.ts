import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import test from "node:test";

import { apiGet, configureApiRuntime, resetApiRuntime } from "@agw/api";

import { extensionServerPath, isExtensionVersionMismatch } from "./server-extension-request";

test("extensionServerPath preserves only paths under the extension prefix", () => {
  assert.equal(
    extensionServerPath("calendar", "/appointments/today"),
    "/api/ext/calendar/appointments/today",
  );
  assert.equal(
    extensionServerPath("calendar", "appointments/Meeting Room"),
    "/api/ext/calendar/appointments/Meeting%20Room",
  );
});

test("extensionServerPath allows the Notifications answer endpoint only on POST", () => {
  assert.equal(
    extensionServerPath("taskpilot", "/api/notifications/answer", "POST"),
    "/api/notifications/answer",
  );
  assert.throws(() => extensionServerPath("taskpilot", "/api/notifications/answer", "GET"), {
    code: "ERR_INVALID_ARGUMENT",
  });
  assert.equal(
    extensionServerPath("taskpilot", "/api/notifications/get", "GET"),
    "/api/ext/taskpilot/api/notifications/get",
  );
});

test("extensionServerPath rejects path escapes and absolute URLs", () => {
  for (const path of ["../admin", "%2e%2e/admin", "appointments%2fadmin", "https://example.com"]) {
    assert.throws(() => extensionServerPath("calendar", path), { code: "ERR_INVALID_ARGUMENT" });
  }
});

test("version mismatch detection requires the Server's exact error code", async () => {
  const server = createServer((request, response) => {
    const url = new URL(request.url ?? "/", "http://localhost");
    response.writeHead(409, { "content-type": "application/json" });
    response.end(JSON.stringify({ code: Number(url.searchParams.get("code")), title: "Conflict" }));
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  const baseUrl = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  configureApiRuntime({ baseUrl, token: null });

  try {
    const request = async (code: number): Promise<unknown> => {
      try {
        await apiGet(
          "/api/projects" as never,
          {
            params: { query: { code } },
          } as never,
        );
      } catch (error) {
        return error;
      }
      throw new Error("The conflict request unexpectedly succeeded.");
    };

    assert.equal(isExtensionVersionMismatch(await request(4090027)), true);
    assert.equal(isExtensionVersionMismatch(await request(4090028)), false);
  } finally {
    resetApiRuntime();
    server.close();
    await once(server, "close");
  }
});
