import assert from "node:assert/strict";
import test from "node:test";
import { validate, version } from "uuid";

test("createUuidV7 returns a valid UUID version 7", async () => {
  const uuidModule = await import("./uuid.ts").catch(() => null);

  assert.ok(uuidModule, "UUID v7 generator should exist");
  const value = uuidModule.createUuidV7();

  assert.equal(validate(value), true);
  assert.equal(version(value), 7);
});
