import assert from "node:assert/strict";
import {
  lstat,
  mkdir,
  readdir,
  readFile,
  rename,
  symlink,
  truncate,
  writeFile,
} from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { pathToFileURL } from "node:url";

import type { ExtensionEvent } from "../../shared/contracts";
import type { ExtensionPermission } from "../../shared/extension-permissions";
import { createScratchDirectory } from "../scratch-directory.test-helper";
import { ExtensionAccessGrants } from "./extension-access-grants";
import {
  ExtensionFileSystem,
  type FileAccessContext,
  MAX_FILE_TRANSFER_BYTES,
} from "./extension-file-system";
import { ExtensionFileWatcher, MAX_WATCHERS_PER_EXTENSION } from "./extension-file-watcher";

const uri = (path: string) => pathToFileURL(path).toString();
const text = (value: string) => new TextEncoder().encode(value);

async function createHarness(permissions: ExtensionPermission[] = []) {
  const scratch = await createScratchDirectory("file-system");
  const storage = join(scratch.path, "storage");
  const outside = join(scratch.path, "outside");
  await mkdir(storage, { recursive: true });
  await mkdir(outside, { recursive: true });
  const grants = new ExtensionAccessGrants();
  const fileSystem = new ExtensionFileSystem(grants);
  const context: FileAccessContext = {
    extensionId: "sample",
    grantedPermissions: permissions,
    storageDirectory: storage,
  };
  return { scratch, storage, outside, grants, fileSystem, context, dispose: scratch.dispose };
}

async function denied(promise: Promise<unknown>): Promise<void> {
  await assert.rejects(promise, { code: "ERR_PERMISSION_DENIED" });
}

test("every operation works inside the storage directory without any permission", async () => {
  const { storage, fileSystem, context, dispose } = await createHarness();
  try {
    const file = join(storage, "config.json");
    await fileSystem.execute(context, {
      operation: "writeFile",
      uri: uri(file),
      data: text("{}"),
      overwrite: true,
      createParents: true,
    });
    assert.deepEqual(
      await fileSystem.execute(context, { operation: "readFile", uri: uri(file) }),
      text("{}"),
    );
    const info = await fileSystem.execute(context, { operation: "stat", uri: uri(file) });
    assert.equal(info.type, "file");
    assert.equal(info.size, 2);
    assert.match(info.modifiedTime!, /Z$/u);
    assert.equal(await fileSystem.execute(context, { operation: "exists", uri: uri(file) }), true);
    assert.equal(
      await fileSystem.execute(context, { operation: "exists", uri: uri(join(storage, "nope")) }),
      false,
    );
    await fileSystem.execute(context, {
      operation: "createDirectory",
      uri: uri(join(storage, "dir")),
      recursive: false,
    });
    assert.deepEqual(
      (await fileSystem.execute(context, { operation: "readDirectory", uri: uri(storage) })).sort(
        (a, b) => a.name.localeCompare(b.name),
      ),
      [
        { name: "config.json", uri: uri(file), type: "file" },
        { name: "dir", uri: uri(join(storage, "dir")), type: "directory" },
      ],
    );
    await fileSystem.execute(context, {
      operation: "rename",
      from: uri(file),
      to: uri(join(storage, "dir", "moved.json")),
      overwrite: false,
    });
    await assert.rejects(
      fileSystem.execute(context, {
        operation: "delete",
        uri: uri(join(storage, "dir")),
        recursive: false,
      }),
      { code: "ENOTEMPTY" },
    );
    await fileSystem.execute(context, {
      operation: "delete",
      uri: uri(join(storage, "dir")),
      recursive: true,
    });
    assert.deepEqual(await readdir(storage), []);
  } finally {
    await dispose();
  }
});

test("node error codes are preserved and existing targets are protected when not overwriting", async () => {
  const { storage, fileSystem, context, dispose } = await createHarness();
  try {
    await assert.rejects(
      fileSystem.execute(context, {
        operation: "readFile",
        uri: uri(join(storage, "missing.txt")),
      }),
      { code: "ENOENT", syscall: "stat" },
    );
    await writeFile(join(storage, "a.txt"), "a");
    await writeFile(join(storage, "b.txt"), "b");
    await assert.rejects(
      fileSystem.execute(context, {
        operation: "writeFile",
        uri: uri(join(storage, "a.txt")),
        data: text("x"),
        overwrite: false,
        createParents: true,
      }),
      { code: "EEXIST" },
    );
    await assert.rejects(
      fileSystem.execute(context, {
        operation: "rename",
        from: uri(join(storage, "a.txt")),
        to: uri(join(storage, "b.txt")),
        overwrite: false,
      }),
      { code: "EEXIST" },
    );
    await fileSystem.execute(context, {
      operation: "rename",
      from: uri(join(storage, "a.txt")),
      to: uri(join(storage, "b.txt")),
      overwrite: true,
    });
    assert.equal(await readFile(join(storage, "b.txt"), "utf8"), "a");
    await assert.rejects(
      fileSystem.execute(context, { operation: "readFile", uri: "https://example.com/a" }),
      { code: "ERR_INVALID_URI" },
    );
  } finally {
    await dispose();
  }
});

test("without permissions paths outside the storage directory are denied, even when missing", async () => {
  const { outside, fileSystem, context, dispose } = await createHarness();
  try {
    await writeFile(join(outside, "secret.txt"), "secret");
    await denied(
      fileSystem.execute(context, { operation: "readFile", uri: uri(join(outside, "secret.txt")) }),
    );
    await denied(fileSystem.execute(context, { operation: "readDirectory", uri: uri(outside) }));
    await denied(
      fileSystem.execute(context, { operation: "stat", uri: uri(join(outside, "missing")) }),
    );
    await denied(
      fileSystem.execute(context, { operation: "exists", uri: uri(join(outside, "missing")) }),
    );
    await denied(
      fileSystem.execute(context, {
        operation: "writeFile",
        uri: uri(join(outside, "new.txt")),
        data: text("x"),
        overwrite: true,
        createParents: true,
      }),
    );
    assert.deepEqual(await readdir(outside), ["secret.txt"]);
  } finally {
    await dispose();
  }
});

test("fs.local permissions allow any path for their access kind", async () => {
  const reader = await createHarness(["fs.local.read"]);
  try {
    await writeFile(join(reader.outside, "secret.txt"), "secret");
    assert.deepEqual(
      await reader.fileSystem.execute(reader.context, {
        operation: "readFile",
        uri: uri(join(reader.outside, "secret.txt")),
      }),
      text("secret"),
    );
    await denied(
      reader.fileSystem.execute(reader.context, {
        operation: "delete",
        uri: uri(join(reader.outside, "secret.txt")),
        recursive: false,
      }),
    );
  } finally {
    await reader.dispose();
  }
  const writer = await createHarness(["fs.local.write"]);
  try {
    await writer.fileSystem.execute(writer.context, {
      operation: "writeFile",
      uri: uri(join(writer.outside, "new.txt")),
      data: text("x"),
      overwrite: true,
      createParents: true,
    });
    await denied(
      writer.fileSystem.execute(writer.context, {
        operation: "readFile",
        uri: uri(join(writer.outside, "new.txt")),
      }),
    );
  } finally {
    await writer.dispose();
  }
});

test("open-dialog grants are read-only trees and are cleared on release", async () => {
  const { outside, grants, fileSystem, context, dispose } = await createHarness();
  try {
    await mkdir(join(outside, "notes", "deep"), { recursive: true });
    await writeFile(join(outside, "notes", "deep", "a.md"), "# A");
    await grants.grantRead("sample", join(outside, "notes"));

    assert.deepEqual(
      await fileSystem.execute(context, {
        operation: "readFile",
        uri: uri(join(outside, "notes", "deep", "a.md")),
      }),
      text("# A"),
    );
    await denied(
      fileSystem.execute(context, {
        operation: "writeFile",
        uri: uri(join(outside, "notes", "b.md")),
        data: text("b"),
        overwrite: true,
        createParents: true,
      }),
    );
    await denied(
      fileSystem.execute(context, { operation: "readFile", uri: uri(join(outside, "other.md")) }),
    );

    grants.release("sample");
    await denied(
      fileSystem.execute(context, {
        operation: "readFile",
        uri: uri(join(outside, "notes", "deep", "a.md")),
      }),
    );
  } finally {
    await dispose();
  }
});

test("a save-dialog grant covers only the chosen new file", async () => {
  const { outside, grants, fileSystem, context, dispose } = await createHarness();
  try {
    const target = join(outside, "export.json");
    await grants.grantWrite("sample", target);
    await fileSystem.execute(context, {
      operation: "writeFile",
      uri: uri(target),
      data: text("[1]"),
      overwrite: true,
      createParents: false,
    });
    assert.deepEqual(
      await fileSystem.execute(context, { operation: "readFile", uri: uri(target) }),
      text("[1]"),
    );
    await denied(
      fileSystem.execute(context, {
        operation: "writeFile",
        uri: uri(join(outside, "other.json")),
        data: text("x"),
        overwrite: true,
        createParents: false,
      }),
    );
  } finally {
    await dispose();
  }
});

test("a save-dialog path that is a symbolic link is registered by its target", async () => {
  const { outside, scratch, grants, fileSystem, context, dispose } = await createHarness();
  try {
    const realTarget = join(outside, "real.json");
    const link = join(scratch.path, "link.json");
    await writeFile(realTarget, "{}");
    await symlink(realTarget, link);
    await grants.grantWrite("sample", link);
    await fileSystem.execute(context, {
      operation: "writeFile",
      uri: uri(realTarget),
      data: text("[2]"),
      overwrite: true,
      createParents: false,
    });
    assert.equal(await readFile(realTarget, "utf8"), "[2]");
  } finally {
    await dispose();
  }
});

test("the first write into a missing storage subdirectory creates both levels", async () => {
  const { storage, fileSystem, context, dispose } = await createHarness();
  try {
    const nested = join(storage, "cache", "items", "data.json");
    await assert.rejects(
      fileSystem.execute(context, {
        operation: "writeFile",
        uri: uri(nested),
        data: text("{}"),
        overwrite: true,
        createParents: false,
      }),
      { code: "ENOENT" },
    );
    await fileSystem.execute(context, {
      operation: "writeFile",
      uri: uri(nested),
      data: text("{}"),
      overwrite: true,
      createParents: true,
    });
    assert.equal(await readFile(nested, "utf8"), "{}");
  } finally {
    await dispose();
  }
});

test("a symbolic link in the storage directory cannot reach outside, and deleting it removes only the link", async () => {
  const { storage, outside, fileSystem, context, dispose } = await createHarness();
  try {
    await writeFile(join(outside, "secret.txt"), "secret");
    await symlink(outside, join(storage, "link"));
    await symlink(join(outside, "absent.txt"), join(storage, "dangling"));

    await denied(
      fileSystem.execute(context, {
        operation: "readFile",
        uri: uri(join(storage, "link", "secret.txt")),
      }),
    );
    await denied(
      fileSystem.execute(context, {
        operation: "writeFile",
        uri: uri(join(storage, "link", "new.txt")),
        data: text("x"),
        overwrite: true,
        createParents: true,
      }),
    );
    await denied(
      fileSystem.execute(context, {
        operation: "writeFile",
        uri: uri(join(storage, "dangling")),
        data: text("x"),
        overwrite: true,
        createParents: true,
      }),
    );
    await denied(
      fileSystem.execute(context, {
        operation: "createDirectory",
        uri: uri(join(storage, "link", "sub")),
        recursive: true,
      }),
    );
    await fileSystem.execute(context, {
      operation: "delete",
      uri: uri(join(storage, "link")),
      recursive: true,
    });
    await assert.rejects(lstat(join(storage, "link")));
    assert.deepEqual(await readdir(outside), ["secret.txt"]);
  } finally {
    await dispose();
  }
});

test("concurrent renames cannot redirect a write outside the allowed range", async () => {
  const { storage, outside, fileSystem, context, dispose } = await createHarness();
  try {
    for (let round = 0; round < 200; round += 1) {
      const base = join(storage, `round-${round}`);
      await mkdir(join(base, "cache"), { recursive: true });
      await symlink(outside, join(base, "link"));
      const results = await Promise.allSettled([
        fileSystem.execute(context, {
          operation: "writeFile",
          uri: uri(join(base, "cache", "file")),
          data: text(String(round)),
          overwrite: true,
          createParents: true,
        }),
        fileSystem.execute(context, {
          operation: "rename",
          from: uri(join(base, "cache")),
          to: uri(join(base, "old")),
          overwrite: false,
        }),
        fileSystem.execute(context, {
          operation: "rename",
          from: uri(join(base, "link")),
          to: uri(join(base, "cache")),
          overwrite: false,
        }),
      ]);
      assert.deepEqual(await readdir(outside), [], `round ${round}`);
      const write = results[0];
      if (write.status === "fulfilled") {
        assert.equal(await readFile(join(base, "old", "file"), "utf8"), String(round));
      } else {
        assert.equal((write.reason as { code?: string }).code, "ERR_PERMISSION_DENIED");
      }
    }
  } finally {
    await dispose();
  }
});

test("reads and writes over 100 MB fail with ERR_FILE_TOO_LARGE", async () => {
  const { storage, fileSystem, context, dispose } = await createHarness();
  try {
    const big = join(storage, "big.bin");
    await writeFile(big, "");
    await truncate(big, MAX_FILE_TRANSFER_BYTES + 1);
    await assert.rejects(fileSystem.execute(context, { operation: "readFile", uri: uri(big) }), {
      code: "ERR_FILE_TOO_LARGE",
    });
    await assert.rejects(
      fileSystem.execute(context, {
        operation: "writeFile",
        uri: uri(join(storage, "out.bin")),
        data: new Uint8Array(MAX_FILE_TRANSFER_BYTES + 1),
        overwrite: true,
        createParents: true,
      }),
      { code: "ERR_FILE_TOO_LARGE" },
    );
  } finally {
    await dispose();
  }
});

test("watching reports created, changed and deleted, and limits watchers per extension", async () => {
  const { storage, dispose } = await createHarness();
  const events: Extract<ExtensionEvent, { type: "file-change" }>[] = [];
  const watcher = new ExtensionFileWatcher((event) => events.push(event));
  try {
    const owner = { extensionId: "sample", frameId: "frame-1", pageId: "page-1" };
    // macOS 的 FSEvents 会让新建文件随后的修改也带有创建标志，因此修改监听开始前就存在的文件。
    // macOS FSEvents keeps the created flag on a new file's later edits, so edit a file that predates the watcher.
    const existing = join(storage, "existing.txt");
    await writeFile(existing, "before");
    await new Promise((resolve) => setTimeout(resolve, 200));
    await watcher.add(owner, "w1", storage, true);
    const waitFor = async (type: string, path: string) => {
      const target = uri(path);
      for (let attempt = 0; attempt < 100; attempt += 1) {
        if (events.some((event) => event.event.type === type && event.event.uri === target)) return;
        await new Promise((resolve) => setTimeout(resolve, 20));
      }
      assert.fail(
        `No ${type} event for ${path}: ${JSON.stringify(events.map((event) => event.event))}`,
      );
    };
    await writeFile(existing, "after");
    await waitFor("changed", existing);
    const file = join(storage, "watched.txt");
    await writeFile(file, "one");
    await waitFor("created", file);
    await rename(existing, join(storage, "renamed.txt"));
    await waitFor("deleted", existing);
    assert.ok(events.every((event) => event.watchId === "w1" && event.pageId === "page-1"));

    for (let index = 1; index < MAX_WATCHERS_PER_EXTENSION; index += 1) {
      await watcher.add(owner, `extra-${index}`, storage, false);
    }
    await assert.rejects(watcher.add(owner, "one-too-many", storage, false), {
      code: "ERR_TOO_MANY_WATCHERS",
    });
    await watcher.add(
      { extensionId: "other", frameId: "frame-2", pageId: "page-2" },
      "w1",
      storage,
      false,
    );

    watcher.releasePage("frame-1", "page-1");
    assert.equal(
      watcher.count((item) => item.extensionId === "sample"),
      0,
    );
    assert.equal(
      watcher.count((item) => item.extensionId === "other"),
      1,
    );
  } finally {
    watcher.releaseAll();
    await dispose();
  }
});
