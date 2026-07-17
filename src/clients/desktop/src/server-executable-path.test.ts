import assert from "node:assert/strict";
import test from "node:test";

import { resolveServerExecutablePath } from "./server-executable-path";

test("resolves the published Agw server executable on each platform", () => {
  assert.equal(
    resolveServerExecutablePath("/app/resources", "darwin"),
    "/app/resources/server/agw-server",
  );
  assert.equal(
    resolveServerExecutablePath("/app/resources", "linux"),
    "/app/resources/server/agw-server",
  );
  assert.equal(
    resolveServerExecutablePath("C:\\Agw\\resources", "win32"),
    "C:\\Agw\\resources\\server\\agw-server.exe",
  );
});

test("honors an explicit server override", () => {
  assert.equal(
    resolveServerExecutablePath("/app/resources", "darwin", "/tmp/custom-server"),
    "/tmp/custom-server",
  );
});
