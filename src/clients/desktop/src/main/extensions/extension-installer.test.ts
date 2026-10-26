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

/** 批准核对通过。The approval check passes. */
const approved = async () => undefined;

const EXTENSION_ID = "01a173c6-4934-746b-b76c-3e397c297940";
const NESTED_EXTENSION_ID = "01a173c6-4934-746b-b76c-417ece4ebb8f";

type Harness = {
  root: string;
  packages: string;
  data: string;
  sources: string;
  settings: DesktopSettingsStore;
  installer: ExtensionInstaller;
  clearedSessions: string[];
  dispose(): Promise<void>;
};

async function createHarness(archiveLimits?: ArchiveLimits): Promise<Harness> {
  const scratch = await createScratchDirectory("installer");
  const packages = join(scratch.path, "extensions", "packages");
  const data = join(scratch.path, "extensions", "data");
  const sources = join(scratch.path, "sources");
  await mkdir(packages, { recursive: true });
  await mkdir(sources, { recursive: true });
  const settings = new DesktopSettingsStore(scratch.path, "client", codec);
  const clearedSessions: string[] = [];
  const installer = new ExtensionInstaller({
    packagesDirectory: packages,
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
    packages,
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
    return await readdir(join(harness.packages, ".staging"));
  } catch {
    return [];
  }
}

async function installedVersion(harness: Harness, id: string): Promise<string> {
  const manifest = JSON.parse(await readFile(join(harness.packages, id, "manifest.json"), "utf8"));
  return manifest.version as string;
}

const grants = (manifest: Record<string, unknown>): ExtensionGrants => ({
  permissions: manifest.permissions as ExtensionGrants["permissions"],
  hostPermissions: manifest.hostPermissions as string[],
});

test("installing from a folder copies the package and commits it", async () => {
  const harness = await createHarness();
  try {
    const source = join(harness.sources, EXTENSION_ID);
    await writeTestExtension(source, testManifest(EXTENSION_ID), { "assets/app.js": "export {};" });

    const preview = await harness.installer.prepareFromDirectory(source);
    assert.deepEqual(preview.errors, []);
    assert.equal(preview.manifest?.id, EXTENSION_ID);
    assert.equal(preview.installedVersion, null);

    const installed = await harness.installer.commit(preview.installId, approved);
    assert.equal(installed.validation, "valid");
    assert.equal(
      await readFile(join(harness.packages, EXTENSION_ID, "assets", "app.js"), "utf8"),
      "export {};",
    );
    assert.deepEqual(await stagingEntries(harness), []);
    assert.deepEqual((await harness.settings.load()).extensions, {});
  } finally {
    await harness.dispose();
  }
});

test("a failed approval check commits nothing and keeps the staged package", async () => {
  const harness = await createHarness();
  try {
    const source = join(harness.sources, EXTENSION_ID);
    await writeTestExtension(source, testManifest(EXTENSION_ID));
    const preview = await harness.installer.prepareFromDirectory(source);
    const { directory, entry } = await harness.installer.inspect(preview.installId);
    assert.equal(entry.manifest?.id, EXTENSION_ID);
    await writeFile(join(directory, "added.js"), "export {};");

    await assert.rejects(
      harness.installer.commit(preview.installId, async () => {
        throw new Error("The extension package changed after it was approved.");
      }),
      /changed after it was approved/u,
    );
    assert.deepEqual(
      await readdir(harness.packages).then((names) =>
        names.filter((name) => !name.startsWith(".")),
      ),
      [],
    );
    assert.deepEqual(await stagingEntries(harness), [preview.installId]);
  } finally {
    await harness.dispose();
  }
});

test("a folder containing a symbolic link is rejected and the staging directory removed", async () => {
  const harness = await createHarness();
  try {
    const source = join(harness.sources, EXTENSION_ID);
    await writeTestExtension(source, testManifest(EXTENSION_ID));
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
    await writeZip(atRoot, extensionEntries("", EXTENSION_ID));
    const rootPreview = await harness.installer.prepareFromArchive(atRoot);
    assert.deepEqual(rootPreview.errors, []);
    await harness.installer.commit(rootPreview.installId, approved);
    assert.equal(await installedVersion(harness, EXTENSION_ID), "1.0.0");

    const nested = join(harness.sources, "nested.zip");
    await writeZip(nested, [
      ...extensionEntries("nested-ext/", NESTED_EXTENSION_ID),
      { name: "__MACOSX/._manifest.json", content: "metadata" },
    ]);
    const nestedPreview = await harness.installer.prepareFromArchive(nested);
    assert.deepEqual(nestedPreview.errors, []);
    await harness.installer.commit(nestedPreview.installId, approved);
    assert.equal(await installedVersion(harness, NESTED_EXTENSION_ID), "1.0.0");

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
    const first = testManifest(EXTENSION_ID, { version: "1.0.0" });
    await writeTestExtension(join(harness.sources, "v1"), first);
    await harness.installer.commit(
      (await harness.installer.prepareFromDirectory(join(harness.sources, "v1"))).installId,
      approved,
    );
    await harness.settings.setExtensionEnabled(EXTENSION_ID, true, grants(first));

    const second = testManifest(EXTENSION_ID, { version: "1.1.0", hostPermissions: [] });
    await writeTestExtension(join(harness.sources, "v2"), second);
    const preview = await harness.installer.prepareFromDirectory(join(harness.sources, "v2"));
    assert.equal(preview.installedVersion, "1.0.0");
    await harness.installer.commit(preview.installId, approved);
    assert.deepEqual((await harness.settings.load()).extensions[EXTENSION_ID], {
      enabled: true,
      grantedPermissions: ["context.project"],
      grantedHostPermissions: [],
      lastActivatedVersion: null,
    });

    const third = testManifest(EXTENSION_ID, {
      version: "1.2.0",
      permissions: ["context.project", "execution"],
    });
    await writeTestExtension(join(harness.sources, "v3"), third);
    await harness.installer.commit(
      (await harness.installer.prepareFromDirectory(join(harness.sources, "v3"))).installId,
      approved,
    );
    const saved = (await harness.settings.load()).extensions[EXTENSION_ID];
    assert.equal(saved.enabled, false);
    assert.deepEqual(saved.grantedPermissions, ["context.project"]);
    assert.equal(await installedVersion(harness, EXTENSION_ID), "1.2.0");
  } finally {
    await harness.dispose();
  }
});

test("an update replaces only packages/<id> and keeps data/<id>", async () => {
  const harness = await createHarness();
  try {
    await writeTestExtension(
      join(harness.sources, "v1"),
      testManifest(EXTENSION_ID, { version: "1.0.0" }),
      { "old.js": "export {};" },
    );
    await harness.installer.commit(
      (await harness.installer.prepareFromDirectory(join(harness.sources, "v1"))).installId,
      approved,
    );
    await mkdir(join(harness.data, EXTENSION_ID), { recursive: true });
    await writeFile(join(harness.data, EXTENSION_ID, "config.json"), '{"theme":"dark"}');

    // 包自己的 data/ 目录只是包文件，与数据目录无关。A package's own data/ folder is a package file, not the data directory.
    await writeTestExtension(
      join(harness.sources, "v2"),
      testManifest(EXTENSION_ID, { version: "2.0.0" }),
      { "data/defaults.json": "{}" },
    );
    await harness.installer.commit(
      (await harness.installer.prepareFromDirectory(join(harness.sources, "v2"))).installId,
      approved,
    );

    assert.equal(await installedVersion(harness, EXTENSION_ID), "2.0.0");
    await assert.rejects(readFile(join(harness.packages, EXTENSION_ID, "old.js"), "utf8"));
    assert.equal(
      await readFile(join(harness.packages, EXTENSION_ID, "data", "defaults.json"), "utf8"),
      "{}",
    );
    assert.equal(
      await readFile(join(harness.data, EXTENSION_ID, "config.json"), "utf8"),
      '{"theme":"dark"}',
    );
    assert.deepEqual((await readdir(harness.packages)).sort(), [".staging", EXTENSION_ID]);
  } finally {
    await harness.dispose();
  }
});

test("canceling removes the staging directory", async () => {
  const harness = await createHarness();
  try {
    await writeTestExtension(join(harness.sources, EXTENSION_ID), testManifest(EXTENSION_ID));
    const preview = await harness.installer.prepareFromDirectory(
      join(harness.sources, EXTENSION_ID),
    );
    assert.equal((await stagingEntries(harness)).length, 1);
    await harness.installer.cancel(preview.installId);
    assert.deepEqual(await stagingEntries(harness), []);
    await assert.rejects(readdir(join(harness.packages, EXTENSION_ID)));
  } finally {
    await harness.dispose();
  }
});

test("uninstalling removes the package and optionally the data", async () => {
  const harness = await createHarness();
  try {
    for (const id of ["keep-data", "drop-data"]) {
      await writeTestExtension(join(harness.packages, id), testManifest(id));
      await harness.settings.setExtensionEnabled(id, true, grants(testManifest(id)));
      await mkdir(join(harness.data, id), { recursive: true });
      await writeFile(join(harness.data, id, "config.json"), "{}");
    }
    await harness.installer.uninstall("keep-data", false);
    await harness.installer.uninstall("drop-data", true);

    assert.deepEqual(await readdir(harness.packages), [".staging"]);
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
  const oldManifest = testManifest(EXTENSION_ID, {
    version: "1.0.0",
    permissions: ["context.project"],
  });
  const newManifest = testManifest(EXTENSION_ID, { version: "2.0.0", permissions: [] });
  await writeTestExtension(join(harness.packages, EXTENSION_ID), oldManifest);
  await harness.settings.setExtensionEnabled(EXTENSION_ID, true, grants(oldManifest));
  const installId = "00000000-0000-4000-8000-000000000001";
  const staging = join(harness.packages, ".staging", installId);
  await writeTestExtension(join(staging, "package"), newManifest);
  if (step >= 1) {
    await writeJsonFile(join(staging, "journal.json"), {
      operation: "install",
      extensionId: EXTENSION_ID,
      phase: "swapping",
      hasPrevious: true,
    });
  }
  if (step >= 2) await rename(join(harness.packages, EXTENSION_ID), join(staging, "previous"));
  if (step >= 3) await rename(join(staging, "package"), join(harness.packages, EXTENSION_ID));
  if (step >= 4) await harness.settings.applyInstalledExtension(EXTENSION_ID, grants(newManifest));
  if (step >= 5) {
    await writeJsonFile(join(staging, "journal.json"), {
      operation: "install",
      extensionId: EXTENSION_ID,
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
      assert.deepEqual(await readdir(harness.packages).then((names) => names.sort()), [
        ".staging",
        EXTENSION_ID,
      ]);
      assert.deepEqual(await stagingEntries(harness), [], `step ${step}`);
      assert.equal(await installedVersion(harness, EXTENSION_ID), version, `step ${step}`);
      const saved = (await harness.settings.load()).extensions[EXTENSION_ID];
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
      await writeTestExtension(join(harness.packages, EXTENSION_ID), testManifest(EXTENSION_ID));
      await harness.settings.setExtensionEnabled(
        EXTENSION_ID,
        true,
        grants(testManifest(EXTENSION_ID)),
      );
      await mkdir(join(harness.data, EXTENSION_ID), { recursive: true });
      const uninstallId = "00000000-0000-4000-8000-000000000002";
      const staging = join(harness.packages, ".staging", uninstallId);
      await writeJsonFile(join(staging, "journal.json"), {
        operation: "uninstall",
        extensionId: EXTENSION_ID,
        deleteData: true,
        phase: "removing",
      });
      if (step >= 2) await rename(join(harness.packages, EXTENSION_ID), join(staging, "previous"));

      await harness.installer.recover();

      assert.deepEqual(await stagingEntries(harness), [], `step ${step}`);
      const names = await readdir(harness.packages);
      if (step === 1) {
        assert.deepEqual(names.sort(), [".staging", EXTENSION_ID]);
        assert.equal((await harness.settings.load()).extensions[EXTENSION_ID].enabled, true);
      } else {
        assert.deepEqual(names, [".staging"]);
        assert.deepEqual((await harness.settings.load()).extensions, {});
        await assert.rejects(readdir(join(harness.data, EXTENSION_ID)));
        assert.deepEqual(harness.clearedSessions, [EXTENSION_ID]);
      }
    } finally {
      await harness.dispose();
    }
  }
});
