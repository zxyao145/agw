import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { pathToFileURL } from "node:url";
import { parseHostPermissions } from "../../shared/extension-host-permissions";

import { createScratchDirectory } from "../scratch-directory.test-helper";
import { ExtensionAccessGrants } from "./extension-access-grants";
import { ExtensionFrameRegistry } from "./extension-frame-registry";
import { ExtensionFileSystem } from "./extension-file-system";
import { ReplacedFileRegistry } from "./replaced-file-registry";
import { isRequestAllowed } from "./extension-network-rules";

test("selection grants remain available until the extension releases them", async () => {
  const scratch = await createScratchDirectory("extension-frame-grants");
  try {
    const extensionId = "server-" + "a".repeat(64);
    const path = join(scratch.path, "selected.txt");
    const grants = new ExtensionAccessGrants();

    await grants.grantRead(extensionId, path);

    assert.equal(grants.allows(extensionId, path, "read"), true);
    grants.release(extensionId);
    assert.equal(grants.allows(extensionId, path, "read"), false);
  } finally {
    await scratch.dispose();
  }
});

test("disabled extensions without an approved frame reject capabilities before frame lookup", () => {
  const registry = new ExtensionFrameRegistry();
  const caller = {
    instanceId: "sample",
    extensionId: "sample",
    frameId: "missing",
    pageId: "missing",
  };
  assert.throws(() => registry.requireGrants(caller, false), {
    code: "ERR_EXTENSION_DISABLED",
  });
  assert.throws(() => registry.requireGrants(caller, true), { code: "ERR_FRAME_GONE" });
});

test("retained Server frames can export using approved permissions while a new frame awaits approval", async () => {
  const scratch = await createScratchDirectory("retained-frame-export");
  try {
    const registry = new ExtensionFrameRegistry();
    const instanceId = "server-" + "a".repeat(64);
    const extensionId = "01a173c6-4933-766f-8182-77f77ba5b946";
    const frame = registry.register({
      instanceId,
      extensionId,
      frameId: "old",
      origin: { serverId: "server-a", userId: "alice" },
      releaseHash: "a".repeat(64),
      kind: "mainView",
      viewId: "editor",
      webContentsId: 1,
    });
    registry.recordHello(1);
    registry.approveGrants(frame.frameId, {
      permissions: ["fs.local.write"],
      hostPermissions: ["https://old.example.com"],
    });
    const caller = { instanceId, extensionId, frameId: frame.frameId, pageId: frame.pageId! };
    registry.register({
      instanceId,
      extensionId,
      frameId: "new",
      origin: frame.origin,
      releaseHash: "b".repeat(64),
      kind: "mainView",
      viewId: "editor",
      webContentsId: 2,
    });
    const next = registry.recordHello(2)!;
    assert.throws(
      () => registry.requireGrants({ ...caller, frameId: "new", pageId: next.pageId }, false),
      { code: "ERR_EXTENSION_DISABLED" },
    );
    assert.throws(() => registry.requireGrants({ ...caller, pageId: "replaced" }, false), {
      code: "ERR_FRAME_GONE",
    });
    const files = new ExtensionFileSystem(new ExtensionAccessGrants(), new ReplacedFileRegistry());
    const path = join(scratch.path, "export.txt");
    await files.execute(
      {
        extensionId: instanceId,
        grantedPermissions: registry.requireGrants(caller, false).permissions,
        storageDirectory: join(scratch.path, "storage"),
      },
      {
        operation: "writeFile",
        uri: pathToFileURL(path).toString(),
        data: new TextEncoder().encode("saved"),
        overwrite: false,
        createParents: true,
      },
    );
    assert.equal(await readFile(path, "utf8"), "saved");
    registry.approveGrants("new", {
      permissions: ["fs.local.read"],
      hostPermissions: ["https://new.example.com"],
    });
    const hosts = parseHostPermissions(registry.requireGrants(caller, true).hostPermissions);
    assert.equal(isRequestAllowed(instanceId, hosts, "https://old.example.com/data"), true);
    assert.equal(isRequestAllowed(instanceId, hosts, "https://new.example.com/data"), false);
    registry.revokeGrants(instanceId);
    assert.throws(() => registry.requireGrants(caller, false), {
      code: "ERR_EXTENSION_DISABLED",
    });
  } finally {
    await scratch.dispose();
  }
});
