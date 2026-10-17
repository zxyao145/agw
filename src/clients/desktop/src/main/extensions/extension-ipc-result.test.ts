import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

import { ExtensionError, fromExtensionErrorData } from "../../shared/extension-rpc";
import { createScratchDirectory } from "../scratch-directory.test-helper";
import { toExtensionIpcResult } from "./extension-ipc-result";

test("Node.js errors survive structured cloning and are rebuilt as ExtensionError", async () => {
  const scratch = await createScratchDirectory("ipc-result");
  try {
    const missing = join(scratch.path, "missing.txt");
    const original = await readFile(missing).catch((error: NodeJS.ErrnoException) => error);
    assert.ok(original instanceof Error);

    const result = structuredClone(await toExtensionIpcResult(() => readFile(missing)));
    assert.equal(result.ok, false);
    if (result.ok) return;
    const rebuilt = fromExtensionErrorData(result.error);

    assert.ok(rebuilt instanceof ExtensionError);
    assert.equal(rebuilt.name, "ExtensionError");
    assert.equal(result.error.name, original.name);
    assert.equal(rebuilt.message, original.message);
    assert.equal(rebuilt.code, original.code);
    assert.equal(rebuilt.syscall, original.syscall);
    assert.equal(rebuilt.path, original.path);
    assert.equal(rebuilt.code, "ENOENT");
  } finally {
    await scratch.dispose();
  }
});

test("thrown values that are not errors have a null code", async () => {
  const result = structuredClone(
    await toExtensionIpcResult(async () => {
      throw "plain failure";
    }),
  );
  assert.deepEqual(result, {
    ok: false,
    error: { name: "Error", message: "plain failure", code: null, syscall: null, path: null },
  });
  if (!result.ok) assert.equal(fromExtensionErrorData(result.error).code, null);
});

test("successful operations return their value", async () => {
  assert.deepEqual(await toExtensionIpcResult(async () => new Uint8Array([1, 2])), {
    ok: true,
    value: new Uint8Array([1, 2]),
  });
});
