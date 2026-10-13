import assert from "node:assert/strict";
import { homedir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { getServerDataDirectory, resolveServerDataDirectory } from "./server-data-directory";

test("resolveServerDataDirectory uses the platform's Agw application data root", () => {
  assert.equal(
    resolveServerDataDirectory("darwin", "/Users/Ben/Library/Application Support/Agw"),
    "/Users/Ben/Library/Application Support/Agw",
  );
  assert.equal(
    resolveServerDataDirectory("linux", "/home/ben/.local/share/agw"),
    "/home/ben/.local/share/agw",
  );
  assert.equal(
    resolveServerDataDirectory("win32", "D:\\Local AppData\\Agw\\Data"),
    "D:\\Local AppData\\Agw",
  );
});

test("resolveServerDataDirectory rejects relative and unexpected data roots", () => {
  assert.throws(() => resolveServerDataDirectory("linux", "relative/agw"));
  assert.throws(() => resolveServerDataDirectory("darwin", "/Users/Ben/Library"));
  assert.throws(() => resolveServerDataDirectory("win32", "C:\\Data\\Other\\Data"));
});

test(
  "getServerDataDirectory uses Application Support on macOS",
  { skip: process.platform !== "darwin" },
  () => {
    assert.equal(
      getServerDataDirectory(),
      join(homedir(), "Library", "Application Support", "Agw"),
    );
  },
);
