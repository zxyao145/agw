import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import {
  appendFile,
  chmod,
  lstat,
  mkdir,
  readdir,
  readFile,
  readlink,
  rename,
  rm,
  stat,
  symlink,
  truncate,
  utimes,
  writeFile,
} from "node:fs/promises";
import { basename, dirname, join } from "node:path";
import test from "node:test";
import { pathToFileURL } from "node:url";

import type { ExtensionEvent, ExtensionFileSystemRequest } from "../../shared/contracts";
import type { ExtensionPermission } from "@agw/extension-sdk";
import { createScratchDirectory } from "../scratch-directory.test-helper";
import { ExtensionAccessGrants } from "./extension-access-grants";
import {
  ExtensionFileSystem,
  type FileAccessContext,
  MAX_FILE_TRANSFER_BYTES,
  TEMPORARY_FILE_PATTERN,
} from "./extension-file-system";
import { ExtensionFileWatcher, MAX_WATCHERS_PER_EXTENSION } from "./extension-file-watcher";
import { ReplacedFileRegistry } from "./replaced-file-registry";

const uri = (path: string) => pathToFileURL(path).toString();
const text = (value: string) => new TextEncoder().encode(value);

async function createHarness(permissions: ExtensionPermission[] = []) {
  const scratch = await createScratchDirectory("file-system");
  const storage = join(scratch.path, "storage");
  const outside = join(scratch.path, "outside");
  await mkdir(storage, { recursive: true });
  await mkdir(outside, { recursive: true });
  const grants = new ExtensionAccessGrants();
  const replacedFiles = new ReplacedFileRegistry();
  const fileSystem = new ExtensionFileSystem(grants, replacedFiles);
  const context: FileAccessContext = {
    extensionId: "sample",
    grantedPermissions: permissions,
    storageDirectory: storage,
  };
  return {
    scratch,
    storage,
    outside,
    grants,
    replacedFiles,
    fileSystem,
    context,
    dispose: scratch.dispose,
  };
}

async function denied(promise: Promise<unknown>): Promise<void> {
  await assert.rejects(promise, { code: "ERR_PERMISSION_DENIED" });
}

const sha256 = (value: string) => createHash("sha256").update(value).digest("hex");

function writeRequest(
  path: string,
  data: Uint8Array,
  options: { overwrite?: boolean; expectedSha256?: string; createParents?: boolean } = {},
): Extract<ExtensionFileSystemRequest, { operation: "writeFile" }> {
  return {
    operation: "writeFile",
    uri: uri(path),
    data,
    overwrite: options.overwrite ?? true,
    createParents: options.createParents ?? false,
    expectedSha256: options.expectedSha256,
  };
}

/** 80 MB 的内容，使临时文件的写入跨越多次事件循环。80 MB of content so the temporary file write spans many event-loop turns. */
function largeContent(): Uint8Array {
  return new Uint8Array(80 * 1024 * 1024);
}

async function hasTemporaryFile(target: string): Promise<boolean> {
  const prefix = `.${basename(target)}.agw-`;
  const entries = await readdir(dirname(target));
  return entries.some((entry) => entry.startsWith(prefix) && TEMPORARY_FILE_PATTERN.test(entry));
}

function settledFlag(write: Promise<unknown>): { settled(): boolean; observed: Promise<unknown> } {
  let settled = false;
  const observed = write.then(
    () => (settled = true),
    () => (settled = true),
  );
  return { settled: () => settled, observed };
}

/**
 * 临时文件出现时第 1 步已经完成；此时由外部执行 action，模拟其他程序在写入期间修改目标。
 * Once the temporary file exists step 1 is done; action then runs from outside, like another program changing the target mid-write.
 */
async function duringTemporaryWrite(
  target: string,
  write: Promise<unknown>,
  action: () => Promise<void>,
): Promise<void> {
  const { settled, observed } = settledFlag(write);
  while (!settled()) {
    if (await hasTemporaryFile(target)) {
      await action();
      await observed;
      return;
    }
  }
  assert.fail("The write finished before its temporary file was observed.");
}

/** 写入结束之前是否出现过临时文件。Whether a temporary file appeared before the write settled. */
async function temporaryFileAppeared(target: string, write: Promise<unknown>): Promise<boolean> {
  const { settled, observed } = settledFlag(write);
  let appeared = false;
  while (!settled() && !appeared) appeared = await hasTemporaryFile(target);
  await observed;
  return appeared;
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
  const { storage, replacedFiles, dispose } = await createHarness();
  const events: Extract<ExtensionEvent, { type: "file-change" }>[] = [];
  const watcher = new ExtensionFileWatcher((event) => events.push(event), replacedFiles);
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

test("watching a single file reports changed for every write that replaces it with a rename", async () => {
  const { storage, fileSystem, context, replacedFiles, dispose } = await createHarness();
  const events: Extract<ExtensionEvent, { type: "file-change" }>[] = [];
  const watcher = new ExtensionFileWatcher((event) => events.push(event), replacedFiles);
  try {
    const file = join(storage, "config.json");
    await writeFile(file, "{}");
    // 与上面的测试相同，等待新建文件的 FSEvents 事件过去。As above, let FSEvents events of the new file pass.
    await new Promise((resolve) => setTimeout(resolve, 200));
    await watcher.add(
      { extensionId: "sample", frameId: "frame-1", pageId: "page-1" },
      "w1",
      file,
      false,
    );
    const changed = () => events.filter((event) => event.event.type === "changed").length;
    for (const content of ['{"theme":"light"}', '{"theme":"dark"}']) {
      const before = changed();
      await fileSystem.execute(context, writeRequest(file, text(content)));
      for (let attempt = 0; attempt < 100 && changed() === before; attempt += 1) {
        await new Promise((resolve) => setTimeout(resolve, 20));
      }
      assert.ok(changed() > before, JSON.stringify(events.map((event) => event.event)));
      // 让同一次写入的其余事件到达，再开始下一次写入。Let the rest of this write's events arrive before the next write.
      await new Promise((resolve) => setTimeout(resolve, 300));
    }
    assert.deepEqual(
      events.filter((event) => event.event.type !== "changed" || event.event.uri !== uri(file)),
      [],
    );
  } finally {
    watcher.releaseAll();
    await dispose();
  }
});

type FileChange = Extract<ExtensionEvent, { type: "file-change" }>;

const WATCH_OWNER = { extensionId: "sample", frameId: "frame-1", pageId: "page-1" };

const settle = (milliseconds: number) =>
  new Promise((resolve) => setTimeout(resolve, milliseconds));

/** 等到 `from` 之后出现指定的事件，超时时列出全部事件。Waits for the event after index `from`; on timeout lists every event. */
async function untilEvent(
  events: FileChange[],
  from: number,
  type: string,
  path: string,
): Promise<void> {
  const target = uri(path);
  for (let attempt = 0; attempt < 100; attempt += 1) {
    if (events.slice(from).some((event) => event.event.type === type && event.event.uri === target))
      return;
    await settle(20);
  }
  assert.fail(
    `No ${type} event for ${path}: ${JSON.stringify(events.map((event) => event.event))}`,
  );
}

test("watching a symbolic link to a file reports target changes, writes through the link and the target's deletion", async () => {
  const { storage, fileSystem, context, replacedFiles, dispose } = await createHarness();
  const events: FileChange[] = [];
  const watcher = new ExtensionFileWatcher((event) => events.push(event), replacedFiles);
  try {
    const target = join(storage, "real", "target.txt");
    const link = join(storage, "links", "link.txt");
    await mkdir(dirname(target));
    await mkdir(dirname(link));
    await writeFile(target, "one");
    await symlink(target, link);
    await settle(200);
    await watcher.add(WATCH_OWNER, "w1", link, false);

    let from = events.length;
    await appendFile(target, " two");
    await untilEvent(events, from, "changed", link);
    await settle(300);

    from = events.length;
    await fileSystem.execute(context, writeRequest(link, text("three")));
    await untilEvent(events, from, "changed", link);
    assert.equal(await readFile(target, "utf8"), "three");
    assert.ok((await lstat(link)).isSymbolicLink());
    await settle(300);

    from = events.length;
    await rm(target);
    await untilEvent(events, from, "deleted", link);
    assert.ok(
      events.every((event) => event.event.uri === uri(link)),
      JSON.stringify(events.map((event) => event.event)),
    );
  } finally {
    watcher.releaseAll();
    await dispose();
  }
});

test("watching a file by a path whose case differs from the disk reports its changes", async (t) => {
  const { storage, fileSystem, context, replacedFiles, dispose } = await createHarness();
  const events: FileChange[] = [];
  const watcher = new ExtensionFileWatcher((event) => events.push(event), replacedFiles);
  try {
    const onDisk = join(storage, "Cased.json");
    const requested = join(storage, "CASED.JSON");
    await writeFile(onDisk, "{}");
    const caseInsensitive = await lstat(requested).then(
      () => true,
      (error: NodeJS.ErrnoException) => {
        if (error.code === "ENOENT") return false;
        throw error;
      },
    );
    if (!caseInsensitive) {
      t.skip("the file system is case-sensitive");
      return;
    }
    await settle(200);
    await watcher.add(WATCH_OWNER, "w1", requested, false);
    const from = events.length;
    await fileSystem.execute(context, writeRequest(requested, text('{"a":1}')));
    await untilEvent(events, from, "changed", requested);
    assert.deepEqual(await readdir(storage), ["Cased.json"]);
  } finally {
    watcher.releaseAll();
    await dispose();
  }
});

test("directory watches report the written files but never Agw's temporary files", async () => {
  const { storage, fileSystem, context, replacedFiles, dispose } = await createHarness();
  const events: FileChange[] = [];
  const watcher = new ExtensionFileWatcher((event) => events.push(event), replacedFiles);
  try {
    const existing = join(storage, "existing.txt");
    const created = join(storage, "created.txt");
    await writeFile(existing, "before");
    await settle(200);
    await watcher.add(WATCH_OWNER, "w1", storage, true);
    let from = events.length;
    await fileSystem.execute(context, writeRequest(existing, text("after")));
    await untilEvent(events, from, "changed", existing);
    from = events.length;
    await fileSystem.execute(context, writeRequest(created, text("new"), { overwrite: false }));
    await untilEvent(events, from, "created", created);
    await settle(300);
    assert.deepEqual(
      events.filter((event) => event.event.uri.includes(".agw-")),
      [],
      JSON.stringify(events.map((event) => event.event)),
    );
  } finally {
    watcher.releaseAll();
    await dispose();
  }
});

test("a non-recursive directory watch reports changed when Agw replaces an existing file, and created for a new one", async () => {
  const { storage, fileSystem, context, replacedFiles, dispose } = await createHarness();
  const events: FileChange[] = [];
  const watcher = new ExtensionFileWatcher((event) => events.push(event), replacedFiles);
  try {
    const existing = join(storage, "existing.txt");
    const created = join(storage, "created.txt");
    await writeFile(existing, "before");
    await settle(200);
    // 两个监听同一个目录，每个都报告 changed。Two watches on one directory each report changed.
    await watcher.add(WATCH_OWNER, "w1", storage, false);
    await watcher.add(WATCH_OWNER, "w2", storage, false);

    let from = events.length;
    await fileSystem.execute(context, writeRequest(existing, text("after")));
    await untilEvent(events, from, "changed", existing);
    await settle(300);
    const replaceEvents = events.slice(from);
    assert.deepEqual(
      replaceEvents.filter((event) => event.event.type === "created"),
      [],
      JSON.stringify(replaceEvents.map((event) => [event.watchId, event.event])),
    );
    assert.deepEqual([...new Set(replaceEvents.map((event) => event.watchId))].sort(), [
      "w1",
      "w2",
    ]);

    from = events.length;
    await fileSystem.execute(context, writeRequest(created, text("new"), { overwrite: false }));
    await untilEvent(events, from, "created", created);
  } finally {
    watcher.releaseAll();
    await dispose();
  }
});

test("replacements by other programs, and a delete and create after an Agw replacement, still report created", async () => {
  const { storage, fileSystem, context, replacedFiles, dispose } = await createHarness();
  const events: FileChange[] = [];
  const watcher = new ExtensionFileWatcher((event) => events.push(event), replacedFiles);
  try {
    const external = join(storage, "external.txt");
    const recreated = join(storage, "recreated.txt");
    await writeFile(external, "before");
    await writeFile(recreated, "before");
    await settle(200);
    await watcher.add(WATCH_OWNER, "w1", storage, false);
    // Linux 的文件时间按内核时钟节拍更新，比 Date.now() 晚几毫秒；监听开始后立即新建的文件会被当作监听前就存在。
    // Linux file times come from a coarse clock a few ms behind Date.now(); a file created right after the watch started
    // would look older than the watch.
    await settle(100);

    // 其他程序的原子保存不经过 Agw，保持原来的判断。Another program's atomic save bypasses Agw and keeps the usual rule.
    let from = events.length;
    await writeFile(join(storage, "external.next"), "after");
    await rename(join(storage, "external.next"), external);
    await untilEvent(events, from, "created", external);
    await settle(300);

    from = events.length;
    await fileSystem.execute(context, writeRequest(recreated, text("after")));
    await untilEvent(events, from, "changed", recreated);
    await settle(300);
    from = events.length;
    await rm(recreated);
    await untilEvent(events, from, "deleted", recreated);
    await settle(300);
    from = events.length;
    await writeFile(recreated, "again");
    await untilEvent(events, from, "created", recreated);
  } finally {
    watcher.releaseAll();
    await dispose();
  }
});

test("expectedSha256 replaces the file only when the hash on disk matches", async () => {
  const { storage, fileSystem, context, dispose } = await createHarness();
  try {
    const file = join(storage, "note.md");
    await writeFile(file, "original");
    await assert.rejects(
      fileSystem.execute(
        context,
        writeRequest(file, text("other"), { expectedSha256: sha256("stale") }),
      ),
      { code: "ERR_FILE_CHANGED" },
    );
    assert.equal(await readFile(file, "utf8"), "original");

    await fileSystem.execute(
      context,
      writeRequest(file, text("updated"), { expectedSha256: sha256("original") }),
    );
    assert.equal(await readFile(file, "utf8"), "updated");

    await assert.rejects(
      fileSystem.execute(
        context,
        writeRequest(join(storage, "missing.md"), text("new"), {
          expectedSha256: sha256(""),
        }),
      ),
      { code: "ERR_FILE_CHANGED" },
    );
    // 写入前提先于 createParents 检查，不留下空目录。The precondition is checked before createParents, leaving no empty directories.
    await assert.rejects(
      fileSystem.execute(
        context,
        writeRequest(join(storage, "new", "deep", "missing.md"), text("new"), {
          expectedSha256: sha256(""),
          createParents: true,
        }),
      ),
      { code: "ERR_FILE_CHANGED" },
    );
    assert.deepEqual(await readdir(storage), ["note.md"]);
  } finally {
    await dispose();
  }
});

test("expectedSha256 must be 64 lowercase hexadecimal characters and requires overwrite", async () => {
  const { storage, fileSystem, context, dispose } = await createHarness();
  try {
    const file = join(storage, "note.md");
    await writeFile(file, "original");
    for (const options of [
      { expectedSha256: sha256("original").toUpperCase() },
      { expectedSha256: sha256("original").slice(1) },
      { expectedSha256: 42 as unknown as string },
      { expectedSha256: sha256("original"), overwrite: false },
    ]) {
      await assert.rejects(
        fileSystem.execute(context, writeRequest(file, text("x"), options)),
        { code: "ERR_INVALID_ARGUMENT" },
        JSON.stringify(options),
      );
    }
    assert.equal(await readFile(file, "utf8"), "original");
  } finally {
    await dispose();
  }
});

test("two extensions writing one file with the same hash: one succeeds and the other gets ERR_FILE_CHANGED", async () => {
  const { scratch, outside, fileSystem, context, dispose } = await createHarness([
    "fs.local.write",
  ]);
  try {
    const other: FileAccessContext = {
      extensionId: "other",
      grantedPermissions: ["fs.local.write"],
      storageDirectory: join(scratch.path, "other-storage"),
    };
    for (let round = 0; round < 20; round += 1) {
      const file = join(outside, `shared-${round}.txt`);
      await writeFile(file, "base");
      const contents = ["from sample", "from other"];
      const results = await Promise.allSettled([
        fileSystem.execute(
          context,
          writeRequest(file, text(contents[0]), { expectedSha256: sha256("base") }),
        ),
        fileSystem.execute(
          other,
          writeRequest(file, text(contents[1]), { expectedSha256: sha256("base") }),
        ),
      ]);
      const winners = results.flatMap((result, index) =>
        result.status === "fulfilled" ? [contents[index]] : [],
      );
      const losers = results.flatMap((result) =>
        result.status === "rejected" ? [(result.reason as { code?: string }).code] : [],
      );
      assert.equal(winners.length, 1, `round ${round}`);
      assert.deepEqual(losers, ["ERR_FILE_CHANGED"], `round ${round}`);
      assert.equal(await readFile(file, "utf8"), winners[0], `round ${round}`);
    }
    assert.equal((await readdir(outside)).length, 20);
  } finally {
    await dispose();
  }
});

test("a failure before the commit keeps the original content and leaves no temporary file", async () => {
  const { storage, fileSystem, context, dispose } = await createHarness();
  try {
    const file = join(storage, "doc.txt");
    await writeFile(file, "original");
    const write = fileSystem.execute(
      context,
      writeRequest(file, largeContent(), { expectedSha256: sha256("original") }),
    );
    await duringTemporaryWrite(file, write, () =>
      utimes(file, new Date("2001-02-03T04:05:06Z"), new Date("2001-02-03T04:05:06Z")),
    );
    await assert.rejects(write, { code: "ERR_FILE_CHANGED" });
    assert.equal(await readFile(file, "utf8"), "original");
    assert.deepEqual(await readdir(storage), ["doc.txt"]);
  } finally {
    await dispose();
  }
});

test("a directory target, or an existing target without overwrite, fails before any temporary file is written", async () => {
  const { storage, fileSystem, context, dispose } = await createHarness();
  try {
    const directory = join(storage, "folder");
    await mkdir(directory);
    await writeFile(join(directory, "child.txt"), "child");
    const intoDirectory = fileSystem.execute(context, writeRequest(directory, largeContent()));
    assert.equal(await temporaryFileAppeared(directory, intoDirectory), false);
    await assert.rejects(intoDirectory, { code: "EISDIR" });
    assert.deepEqual(await readdir(directory), ["child.txt"]);

    const file = join(storage, "kept.txt");
    await writeFile(file, "kept");
    const withoutOverwrite = fileSystem.execute(
      context,
      writeRequest(file, largeContent(), { overwrite: false }),
    );
    assert.equal(await temporaryFileAppeared(file, withoutOverwrite), false);
    await assert.rejects(withoutOverwrite, { code: "EEXIST" });
    assert.equal(await readFile(file, "utf8"), "kept");
    assert.deepEqual((await readdir(storage)).sort(), ["folder", "kept.txt"]);
  } finally {
    await dispose();
  }
});

test(
  "the mode bits of an existing target are kept",
  { skip: process.platform === "win32" },
  async () => {
    const { storage, fileSystem, context, dispose } = await createHarness();
    try {
      const script = join(storage, "run.sh");
      await writeFile(script, "echo one");
      await chmod(script, 0o750);
      await fileSystem.execute(context, writeRequest(script, text("echo two")));
      assert.equal((await stat(script)).mode & 0o7777, 0o750);

      await chmod(script, 0o604);
      await fileSystem.execute(
        context,
        writeRequest(script, text("echo three"), { expectedSha256: sha256("echo two") }),
      );
      assert.equal((await stat(script)).mode & 0o7777, 0o604);
      assert.equal(await readFile(script, "utf8"), "echo three");
    } finally {
      await dispose();
    }
  },
);

test(
  "a read-only target is not replaced: EACCES names the target before any temporary file is written",
  { skip: process.platform === "win32" },
  async () => {
    const { storage, fileSystem, context, dispose } = await createHarness();
    try {
      const file = join(storage, "locked.txt");
      await writeFile(file, "locked");
      await chmod(file, 0o444);
      const plain = fileSystem.execute(context, writeRequest(file, largeContent()));
      assert.equal(await temporaryFileAppeared(file, plain), false);
      await assert.rejects(plain, { code: "EACCES", path: file });
      await assert.rejects(
        fileSystem.execute(
          context,
          writeRequest(file, text("next"), { expectedSha256: sha256("locked") }),
        ),
        { code: "EACCES", path: file },
      );
      assert.equal(await readFile(file, "utf8"), "locked");
      assert.equal((await stat(file)).mode & 0o7777, 0o444);
      assert.deepEqual(await readdir(storage), ["locked.txt"]);
    } finally {
      await dispose();
    }
  },
);

test("a temporary file that cannot be created is reported with the target path", async () => {
  const { storage, fileSystem, context, dispose } = await createHarness();
  try {
    const file = join(storage, "missing", "doc.txt");
    await assert.rejects(fileSystem.execute(context, writeRequest(file, text("x"))), {
      code: "ENOENT",
      syscall: "open",
      path: file,
      message: `ENOENT: no such file or directory, open '${file}'`,
    });
    assert.deepEqual(await readdir(storage), []);
  } finally {
    await dispose();
  }
});

test("a write removes the temporary files earlier writes left for the same file, and only those", async () => {
  const { storage, fileSystem, context, dispose } = await createHarness();
  try {
    const id = (digit: string) => digit.repeat(32);
    const file = join(storage, "doc.txt");
    await writeFile(file, "original");
    const created = join(storage, "new.txt");
    const leftovers = [`.doc.txt.agw-${id("0")}.tmp`, `.new.txt.agw-${id("1")}.tmp`];
    // 其他文件的临时文件：另一个名称、以本文件的临时文件名开头的名称、不符合格式的名称。
    // Temporary files of other files: another name, a name that starts with this file's temporary name, and a malformed one.
    const kept = [
      `.other.txt.agw-${id("2")}.tmp`,
      `.doc.txt.agw-${id("3")}.tmp.agw-${id("4")}.tmp`,
      `.doc.txt.agw-${id("5")}.tmp.bak`,
    ];
    // 大小写只在 Linux 上区分。Letter case only tells names apart on Linux.
    const otherCase = `.DOC.txt.agw-${id("6")}.tmp`;
    for (const name of [...leftovers, ...kept, otherCase]) {
      await writeFile(join(storage, name), "partial");
    }
    await fileSystem.execute(context, writeRequest(file, text("next")));
    await fileSystem.execute(context, writeRequest(created, text("new"), { overwrite: false }));
    assert.equal(await readFile(file, "utf8"), "next");
    assert.equal(await readFile(created, "utf8"), "new");
    const expected = [...kept, "doc.txt", "new.txt"];
    if (process.platform === "linux") expected.push(otherCase);
    assert.deepEqual((await readdir(storage)).sort(), expected.sort());
  } finally {
    await dispose();
  }
});

test(
  "an earlier temporary file that cannot be deleted fails the write with the target path",
  { skip: process.platform === "win32" },
  async () => {
    const { storage, fileSystem, context, dispose } = await createHarness();
    const directory = join(storage, "locked");
    try {
      const file = join(directory, "doc.txt");
      const leftover = `.doc.txt.agw-${"0".repeat(32)}.tmp`;
      await mkdir(directory);
      await writeFile(file, "original");
      await writeFile(join(directory, leftover), "partial");
      // 目录不可写时其中的文件不能删除，目标文件本身仍可写。Files in a read-only directory cannot be deleted, while
      // the target itself stays writable.
      await chmod(directory, 0o555);
      await assert.rejects(
        fileSystem.execute(context, writeRequest(file, text("next"))),
        (error: NodeJS.ErrnoException) => {
          assert.equal(error.code, "EACCES");
          assert.equal(error.path, file);
          assert.ok(error.message.endsWith(` '${file}'`), error.message);
          assert.ok(!error.message.includes(".agw-"), error.message);
          return true;
        },
      );
      assert.equal(await readFile(file, "utf8"), "original");
      assert.deepEqual((await readdir(directory)).sort(), [leftover, "doc.txt"]);
    } finally {
      await chmod(directory, 0o755);
      await dispose();
    }
  },
);

test("overwrite false fails with EEXIST when the target appears after the check and leaves that file unchanged", async () => {
  const { storage, fileSystem, context, dispose } = await createHarness();
  try {
    const file = join(storage, "created.txt");
    const write = fileSystem.execute(
      context,
      writeRequest(file, largeContent(), { overwrite: false }),
    );
    await duringTemporaryWrite(file, write, () => writeFile(file, "external"));
    await assert.rejects(write, { code: "EEXIST" });
    assert.equal(await readFile(file, "utf8"), "external");
    assert.deepEqual(await readdir(storage), ["created.txt"]);
  } finally {
    await dispose();
  }
});

test("a target replaced by a rename with the same size and modification time is detected by its inode", async () => {
  const { storage, fileSystem, context, dispose } = await createHarness();
  try {
    const file = join(storage, "doc.txt");
    const replacement = join(storage, "doc.next");
    const modified = new Date("2020-01-02T03:04:05Z");
    await writeFile(file, "AAAA");
    await writeFile(replacement, "BBBB");
    await utimes(file, modified, modified);
    await utimes(replacement, modified, modified);
    const [before, next] = await Promise.all([
      lstat(file, { bigint: true }),
      lstat(replacement, { bigint: true }),
    ]);
    assert.equal(before.size, next.size);
    assert.equal(before.mtimeNs, next.mtimeNs);
    assert.notEqual(before.ino, next.ino);

    const write = fileSystem.execute(
      context,
      writeRequest(file, largeContent(), { expectedSha256: sha256("AAAA") }),
    );
    await duringTemporaryWrite(file, write, () => rename(replacement, file));
    await assert.rejects(write, { code: "ERR_FILE_CHANGED" });
    assert.equal(await readFile(file, "utf8"), "BBBB");
    assert.deepEqual(await readdir(storage), ["doc.txt"]);
  } finally {
    await dispose();
  }
});

test("writing through a symbolic link replaces its final target and keeps the link", async () => {
  const { storage, fileSystem, context, dispose } = await createHarness();
  try {
    const target = join(storage, "real.txt");
    const link = join(storage, "link.txt");
    await writeFile(target, "one");
    await symlink(target, link);
    await fileSystem.execute(
      context,
      writeRequest(link, text("two"), { expectedSha256: sha256("one") }),
    );
    assert.equal(await readFile(target, "utf8"), "two");
    assert.ok((await lstat(link)).isSymbolicLink());
    assert.equal(await readlink(link), target);
    assert.deepEqual((await readdir(storage)).sort(), ["link.txt", "real.txt"]);
  } finally {
    await dispose();
  }
});
