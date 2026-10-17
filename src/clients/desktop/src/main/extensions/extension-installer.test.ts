import assert from "node:assert/strict";
import { createWriteStream } from "node:fs";
import { mkdir, readdir, readFile, rename, symlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { pipeline } from "node:stream/promises";
import test from "node:test";
import { ZipFile } from "yazl";

import type { ExtensionGrants } from "../../shared/contracts";
import { createScratchDirectory } from "../scratch-directory.test-helper";
import { DesktopSettingsStore, type SecretCodec } from "../settings/settings-store";
import { writeJsonFile } from "../settings/json-file";
import { ExtensionInstaller, type ArchiveLimits } from "./extension-installer";
import { testManifest, writeTestExtension } from "./extension-fixture.test-helper";

const codec: SecretCodec = {
  encrypt: (value) => Buffer.from(value, "utf8"),
  decrypt: (value) => value.toString("utf8"),
};

type Harness = {
  root: string;
  extensions: string;
  data: string;
  sources: string;
  settings: DesktopSettingsStore;
  installer: ExtensionInstaller;
  clearedSessions: string[];
  dispose(): Promise<void>;
};

async function createHarness(archiveLimits?: ArchiveLimits): Promise<Harness> {
  const scratch = await createScratchDirectory("installer");
  const extensions = join(scratch.path, "extensions");
  const data = join(scratch.path, "extension-data");
  const sources = join(scratch.path, "sources");
  await mkdir(extensions, { recursive: true });
  await mkdir(sources, { recursive: true });
  const settings = new DesktopSettingsStore(scratch.path, "client", codec);
  const clearedSessions: string[] = [];
  const installer = new ExtensionInstaller({
    extensionsDirectory: extensions,
    dataDirectory: data,
    appVersion: "0.5.0",
    settings,
    clearSessionData: async (id) => {
      clearedSessions.push(id);
    },
    archiveLimits,
  });
  return {
    root: scratch.path,
    extensions,
    data,
    sources,
    settings,
    installer,
    clearedSessions,
    dispose: scratch.dispose,
  };
}

async function writeZip(
  file: string,
  entries: { name: string; content?: string; mode?: number; directory?: boolean }[],
  rewrite?: [string, string],
): Promise<void> {
  const zip = new ZipFile();
  for (const entry of entries) {
    if (entry.directory) zip.addEmptyDirectory(entry.name);
    else
      zip.addBuffer(Buffer.from(entry.content ?? ""), entry.name, { mode: entry.mode ?? 0o100644 });
  }
  zip.end();
  await pipeline(zip.outputStream, createWriteStream(file));
  if (rewrite) {
    // 文件名不参与 CRC，同长度替换得到 yazl 拒绝生成的条目名。
    // File names are not covered by the CRC, so a same-length replacement yields names yazl refuses to write.
    const [from, to] = rewrite.map((value) => Buffer.from(value));
    assert.equal(from.length, to.length);
    const bytes = await readFile(file);
    let index = bytes.indexOf(from);
    assert.notEqual(index, -1);
    while (index !== -1) {
      to.copy(bytes, index);
      index = bytes.indexOf(from, index + to.length);
    }
    await writeFile(file, bytes);
  }
}

function extensionEntries(prefix: string, id: string): { name: string; content: string }[] {
  return [
    { name: `${prefix}manifest.json`, content: JSON.stringify(testManifest(id)) },
    { name: `${prefix}icon.svg`, content: '<svg xmlns="http://www.w3.org/2000/svg"/>' },
    { name: `${prefix}background.js`, content: "export {};" },
    { name: `${prefix}index.html`, content: "<!doctype html>" },
  ];
}

async function stagingEntries(harness: Harness): Promise<string[]> {
  try {
    return await readdir(join(harness.extensions, ".staging"));
  } catch {
    return [];
  }
}

async function installedVersion(harness: Harness, id: string): Promise<string> {
  const manifest = JSON.parse(
    await readFile(join(harness.extensions, id, "manifest.json"), "utf8"),
  );
  return manifest.version as string;
}

const grants = (manifest: Record<string, unknown>): ExtensionGrants => ({
  permissions: manifest.permissions as ExtensionGrants["permissions"],
  hostPermissions: manifest.hostPermissions as string[],
});

test("installing from a folder copies the package and commits it", async () => {
  const harness = await createHarness();
  try {
    const source = join(harness.sources, "hello");
    await writeTestExtension(source, testManifest("hello"), { "assets/app.js": "export {};" });

    const preview = await harness.installer.prepareFromDirectory(source);
    assert.deepEqual(preview.errors, []);
    assert.equal(preview.manifest?.id, "hello");
    assert.equal(preview.installedVersion, null);

    const installed = await harness.installer.commit(preview.installId);
    assert.equal(installed.validation, "valid");
    assert.equal(
      await readFile(join(harness.extensions, "hello", "assets", "app.js"), "utf8"),
      "export {};",
    );
    assert.deepEqual(await stagingEntries(harness), []);
    assert.deepEqual((await harness.settings.load()).extensions, {});
  } finally {
    await harness.dispose();
  }
});

test("a folder containing a symbolic link is rejected and the staging directory removed", async () => {
  const harness = await createHarness();
  try {
    const source = join(harness.sources, "linked");
    await writeTestExtension(source, testManifest("linked"));
    await symlink(join(source, "index.html"), join(source, "alias.html"));

    const preview = await harness.installer.prepareFromDirectory(source);
    assert.deepEqual(
      preview.errors.map((error) => error.code),
      ["SYMLINK_NOT_ALLOWED"],
    );
    assert.deepEqual(await stagingEntries(harness), []);
  } finally {
    await harness.dispose();
  }
});

test("archives install from the root or from the only top-level folder", async () => {
  const harness = await createHarness();
  try {
    const atRoot = join(harness.sources, "root.zip");
    await writeZip(atRoot, extensionEntries("", "zipped"));
    const rootPreview = await harness.installer.prepareFromArchive(atRoot);
    assert.deepEqual(rootPreview.errors, []);
    await harness.installer.commit(rootPreview.installId);
    assert.equal(await installedVersion(harness, "zipped"), "1.0.0");

    const nested = join(harness.sources, "nested.zip");
    await writeZip(nested, [
      ...extensionEntries("nested-ext/", "nested-ext"),
      { name: "__MACOSX/._manifest.json", content: "metadata" },
    ]);
    const nestedPreview = await harness.installer.prepareFromArchive(nested);
    assert.deepEqual(nestedPreview.errors, []);
    await harness.installer.commit(nestedPreview.installId);
    assert.equal(await installedVersion(harness, "nested-ext"), "1.0.0");

    const twoFolders = join(harness.sources, "two.zip");
    await writeZip(twoFolders, [
      ...extensionEntries("a/", "a-ext"),
      ...extensionEntries("b/", "b-ext"),
    ]);
    const twoPreview = await harness.installer.prepareFromArchive(twoFolders);
    assert.deepEqual(
      twoPreview.errors.map((error) => error.code),
      ["MANIFEST_NOT_AT_ROOT"],
    );
    assert.deepEqual(await stagingEntries(harness), []);
  } finally {
    await harness.dispose();
  }
});

test("archives with parent paths, absolute paths or symbolic links are rejected", async () => {
  const harness = await createHarness();
  try {
    const cases: [string, Parameters<typeof writeZip>[1], [string, string] | undefined, string][] =
      [
        [
          "parent.zip",
          [...extensionEntries("", "bad"), { name: "xx/evil.txt", content: "x" }],
          ["xx/evil.txt", "../evil.txt"],
          "ARCHIVE_PATH_NOT_ALLOWED",
        ],
        [
          "absolute.zip",
          [...extensionEntries("", "bad"), { name: "Xevil.txt", content: "x" }],
          ["Xevil.txt", "/evil.txt"],
          "ARCHIVE_PATH_NOT_ALLOWED",
        ],
        [
          "link.zip",
          [
            ...extensionEntries("", "bad"),
            { name: "link.js", content: "/etc/passwd", mode: 0o120777 },
          ],
          undefined,
          "SYMLINK_NOT_ALLOWED",
        ],
      ];
    for (const [name, entries, rewrite, code] of cases) {
      const file = join(harness.sources, name);
      await writeZip(file, entries, rewrite);
      const preview = await harness.installer.prepareFromArchive(file);
      assert.deepEqual(
        preview.errors.map((error) => error.code),
        [code],
        name,
      );
      assert.deepEqual(await stagingEntries(harness), [], name);
    }
    assert.deepEqual(
      await readdir(harness.root).then((names) => names.includes("evil.txt")),
      false,
    );
  } finally {
    await harness.dispose();
  }
});

test("archives over the entry count or extracted size limit stop and remove the staging directory", async () => {
  const harness = await createHarness({ maxEntries: 6, maxBytes: 4096 });
  try {
    const many = join(harness.sources, "many.zip");
    await writeZip(many, [
      ...extensionEntries("", "many"),
      ...Array.from({ length: 3 }, (_, index) => ({ name: `f${index}.txt`, content: "x" })),
    ]);
    assert.deepEqual(
      (await harness.installer.prepareFromArchive(many)).errors.map((error) => error.code),
      ["ARCHIVE_TOO_MANY_ENTRIES"],
    );

    const large = join(harness.sources, "large.zip");
    await writeZip(large, [
      ...extensionEntries("", "large"),
      { name: "big.txt", content: "0".repeat(8192) },
    ]);
    assert.deepEqual(
      (await harness.installer.prepareFromArchive(large)).errors.map((error) => error.code),
      ["ARCHIVE_TOO_LARGE"],
    );
    assert.deepEqual(await stagingEntries(harness), []);
  } finally {
    await harness.dispose();
  }
});

test("replacing keeps the enabled state when grants cover the new declaration and disables otherwise", async () => {
  const harness = await createHarness();
  try {
    const first = testManifest("swap", { version: "1.0.0" });
    await writeTestExtension(join(harness.sources, "v1"), first);
    await harness.installer.commit(
      (await harness.installer.prepareFromDirectory(join(harness.sources, "v1"))).installId,
    );
    await harness.settings.setExtensionEnabled("swap", true, grants(first));

    const second = testManifest("swap", { version: "1.1.0", hostPermissions: [] });
    await writeTestExtension(join(harness.sources, "v2"), second);
    const preview = await harness.installer.prepareFromDirectory(join(harness.sources, "v2"));
    assert.equal(preview.installedVersion, "1.0.0");
    await harness.installer.commit(preview.installId);
    assert.deepEqual((await harness.settings.load()).extensions.swap, {
      enabled: true,
      grantedPermissions: ["context.project"],
      grantedHostPermissions: [],
      lastActivatedVersion: null,
    });

    const third = testManifest("swap", {
      version: "1.2.0",
      permissions: ["context.project", "execution"],
    });
    await writeTestExtension(join(harness.sources, "v3"), third);
    await harness.installer.commit(
      (await harness.installer.prepareFromDirectory(join(harness.sources, "v3"))).installId,
    );
    const saved = (await harness.settings.load()).extensions.swap;
    assert.equal(saved.enabled, false);
    assert.deepEqual(saved.grantedPermissions, ["context.project"]);
    assert.equal(await installedVersion(harness, "swap"), "1.2.0");
  } finally {
    await harness.dispose();
  }
});

test("canceling removes the staging directory", async () => {
  const harness = await createHarness();
  try {
    await writeTestExtension(join(harness.sources, "cancel"), testManifest("cancel"));
    const preview = await harness.installer.prepareFromDirectory(join(harness.sources, "cancel"));
    assert.equal((await stagingEntries(harness)).length, 1);
    await harness.installer.cancel(preview.installId);
    assert.deepEqual(await stagingEntries(harness), []);
    await assert.rejects(readdir(join(harness.extensions, "cancel")));
  } finally {
    await harness.dispose();
  }
});

test("uninstalling removes the package and optionally the data", async () => {
  const harness = await createHarness();
  try {
    for (const id of ["keep-data", "drop-data"]) {
      await writeTestExtension(join(harness.extensions, id), testManifest(id));
      await harness.settings.setExtensionEnabled(id, true, grants(testManifest(id)));
      await mkdir(join(harness.data, id), { recursive: true });
      await writeFile(join(harness.data, id, "config.json"), "{}");
    }
    await harness.installer.uninstall("keep-data", false);
    await harness.installer.uninstall("drop-data", true);

    assert.deepEqual(await readdir(harness.extensions), [".staging"]);
    assert.deepEqual(await stagingEntries(harness), []);
    assert.deepEqual((await harness.settings.load()).extensions, {});
    assert.equal(await readFile(join(harness.data, "keep-data", "config.json"), "utf8"), "{}");
    await assert.rejects(readdir(join(harness.data, "drop-data")));
    assert.deepEqual(harness.clearedSessions, ["drop-data"]);
  } finally {
    await harness.dispose();
  }
});

/** 构造某一步之后中断的替换安装。Builds a replacement install interrupted after a given step. */
async function interruptedReplacement(harness: Harness, step: number): Promise<void> {
  const oldManifest = testManifest("crash", { version: "1.0.0", permissions: ["context.project"] });
  const newManifest = testManifest("crash", { version: "2.0.0", permissions: [] });
  await writeTestExtension(join(harness.extensions, "crash"), oldManifest);
  await harness.settings.setExtensionEnabled("crash", true, grants(oldManifest));
  const installId = "00000000-0000-4000-8000-000000000001";
  const staging = join(harness.extensions, ".staging", installId);
  await writeTestExtension(join(staging, "package"), newManifest);
  if (step >= 1) {
    await writeJsonFile(join(staging, "journal.json"), {
      operation: "install",
      extensionId: "crash",
      phase: "swapping",
      hasPrevious: true,
    });
  }
  if (step >= 2) await rename(join(harness.extensions, "crash"), join(staging, "previous"));
  if (step >= 3) await rename(join(staging, "package"), join(harness.extensions, "crash"));
  if (step >= 4) await harness.settings.applyInstalledExtension("crash", grants(newManifest));
  if (step >= 5) {
    await writeJsonFile(join(staging, "journal.json"), {
      operation: "install",
      extensionId: "crash",
      phase: "committed",
      hasPrevious: true,
    });
  }
}

test("startup recovery restores the old version or completes the new one after any install step", async () => {
  const expectations: [number, string, string[], string | null][] = [
    [0, "1.0.0", ["context.project"], null],
    [1, "1.0.0", ["context.project"], null],
    [2, "1.0.0", ["context.project"], "restored-previous"],
    [3, "2.0.0", [], "completed"],
    [4, "2.0.0", [], "completed"],
    [5, "2.0.0", [], null],
  ];
  for (const [step, version, permissions, outcome] of expectations) {
    const harness = await createHarness();
    try {
      await interruptedReplacement(harness, step);
      const notices = await harness.installer.recover();

      assert.deepEqual(
        notices.map((notice) => notice.outcome),
        outcome ? [outcome] : [],
        `step ${step}`,
      );
      assert.deepEqual(await readdir(harness.extensions).then((names) => names.sort()), [
        ".staging",
        "crash",
      ]);
      assert.deepEqual(await stagingEntries(harness), [], `step ${step}`);
      assert.equal(await installedVersion(harness, "crash"), version, `step ${step}`);
      const saved = (await harness.settings.load()).extensions.crash;
      assert.equal(saved.enabled, true, `step ${step}`);
      assert.deepEqual(saved.grantedPermissions, permissions, `step ${step}`);
    } finally {
      await harness.dispose();
    }
  }
});

test("startup recovery keeps or finishes an interrupted uninstall", async () => {
  for (const step of [1, 2]) {
    const harness = await createHarness();
    try {
      await writeTestExtension(join(harness.extensions, "gone"), testManifest("gone"));
      await harness.settings.setExtensionEnabled("gone", true, grants(testManifest("gone")));
      await mkdir(join(harness.data, "gone"), { recursive: true });
      const uninstallId = "00000000-0000-4000-8000-000000000002";
      const staging = join(harness.extensions, ".staging", uninstallId);
      await writeJsonFile(join(staging, "journal.json"), {
        operation: "uninstall",
        extensionId: "gone",
        deleteData: true,
        phase: "removing",
      });
      if (step >= 2) await rename(join(harness.extensions, "gone"), join(staging, "previous"));

      await harness.installer.recover();

      assert.deepEqual(await stagingEntries(harness), [], `step ${step}`);
      const names = await readdir(harness.extensions);
      if (step === 1) {
        assert.deepEqual(names.sort(), [".staging", "gone"]);
        assert.equal((await harness.settings.load()).extensions.gone.enabled, true);
      } else {
        assert.deepEqual(names, [".staging"]);
        assert.deepEqual((await harness.settings.load()).extensions, {});
        await assert.rejects(readdir(join(harness.data, "gone")));
        assert.deepEqual(harness.clearedSessions, ["gone"]);
      }
    } finally {
      await harness.dispose();
    }
  }
});
