import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { createWriteStream } from "node:fs";
import { mkdir, readFile, readdir, writeFile } from "node:fs/promises";
import { join, relative, sep } from "node:path";
import { pipeline } from "node:stream/promises";
import test from "node:test";
import yazl from "yazl";
import type { ExtensionManifest } from "@agw/extension-sdk";

import { createScratchDirectory } from "../scratch-directory.test-helper";
import { TEST_ICON_SVG, writeTestExtension } from "./extension-fixture.test-helper";
import { resolveServerExtensionCatalogEntries } from "./server-extension-catalog-sync";
import { ServerExtensionPackageStore } from "./server-extension-package-store";

const APP_VERSION = "0.5.0";
const EXTENSION_ID = "01a173c6-4933-766f-8182-77f77ba5b946";
const FAILED_EXTENSION_ID = "01a173c6-4933-766f-8182-77f77ba5b948";
const CORRUPT_EXTENSION_ID = "01a173c6-4933-766f-8182-77f77ba5b949";
const ORIGIN = { serverId: "01a173c6-4933-766f-8182-77f77ba5b947", userId: "user-a" };
const ENTRY = "<!doctype html><main>Calendar</main>";
const BACKGROUND = 'import { defineBackground } from "/__agw/sdk.js";\ndefineBackground({});\n';
const ROOT_ENTRY = '<!doctype html><script type="module" src="./main.js"></script>';
const SERVER_ENTRY = "server assembly";

async function createArchive(
  source: string,
  archivePath: string,
  compress = true,
): Promise<Uint8Array> {
  const zip = new yazl.ZipFile();
  const files = await readdir(source, { recursive: true, withFileTypes: true });
  for (const entry of files.filter((item) => item.isFile())) {
    const path = join(entry.parentPath, entry.name);
    zip.addFile(path, relative(source, path).split(sep).join("/"), { compress });
  }
  zip.end();
  await pipeline(zip.outputStream, createWriteStream(archivePath));
  return readFile(archivePath);
}

async function createReleaseFixture(
  directory: string,
  archiveName: string,
  compress = true,
  extensionId = EXTENSION_ID,
): Promise<{
  archive: Uint8Array;
  packageHash: string;
  releaseHash: string;
  manifest: ExtensionManifest;
}> {
  const source = join(directory, "source", extensionId);
  const manifest: ExtensionManifest = {
    manifestVersion: 2,
    id: extensionId,
    name: "calendar",
    displayName: "Calendar",
    version: "1.0.0",
    targets: 3,
    files: Object.fromEntries(
      Object.entries({
        "background.js": BACKGROUND,
        "desktop/index.html": ENTRY,
        "icon.svg": TEST_ICON_SVG,
        "index.html": ROOT_ENTRY,
        "server/Calendar.Extension.dll": SERVER_ENTRY,
      }).map(([path, content]) => [path, createHash("sha256").update(content).digest("hex")]),
    ),
    metadata: { owner: "Agw", category: "calendar" },
    desktop: {
      apiVersion: 1,
      contributes: {
        mainViews: [
          { id: "app", title: "Calendar", icon: "Calendar", entry: "desktop/index.html" },
        ],
      },
    },
    server: {
      apiVersion: 1,
      entryAssembly: "server/Calendar.Extension.dll",
      entryType: "Calendar.Extension.Entry",
      metadata: { capabilities: ["calendar"] },
    },
  };
  await writeProjection(source, manifest);
  const manifestBytes = await readFile(join(source, "manifest.json"));
  const archive = await createArchive(source, join(directory, archiveName), compress);
  return {
    archive,
    packageHash: createHash("sha256").update(archive).digest("hex"),
    releaseHash: createHash("sha256").update(manifestBytes).digest("hex"),
    manifest,
  };
}

test("catalog sync records each failed package and keeps valid releases available", async () => {
  const scratch = await createScratchDirectory("server-catalog-sync");
  try {
    const validFixture = await createReleaseFixture(scratch.path, "valid.zip");
    const failedFixture = await createReleaseFixture(
      scratch.path,
      "reported-failure.zip",
      true,
      FAILED_EXTENSION_ID,
    );
    const corruptFixture = await createReleaseFixture(
      scratch.path,
      "corrupt.zip",
      true,
      CORRUPT_EXTENSION_ID,
    );
    const release = (id: string, fixture: Awaited<ReturnType<typeof createReleaseFixture>>) => ({
      id,
      name: fixture.manifest.name,
      displayName: fixture.manifest.displayName ?? fixture.manifest.name,
      version: fixture.manifest.version,
      releaseHash: fixture.releaseHash,
      packageHash: fixture.packageHash,
      manifest: fixture.manifest,
    });
    const releases = [
      release(EXTENSION_ID, validFixture),
      release(FAILED_EXTENSION_ID, failedFixture),
      release(CORRUPT_EXTENSION_ID, corruptFixture),
    ];
    const corruptArchive = Uint8Array.from(corruptFixture.archive);
    corruptArchive[0] ^= 0xff;
    const store = new ServerExtensionPackageStore(join(scratch.path, "cache"), APP_VERSION);

    const entries = await resolveServerExtensionCatalogEntries({
      packages: store,
      origin: ORIGIN,
      catalog: { serverId: ORIGIN.serverId, revision: 7, extensions: releases },
      archives: [
        {
          extensionId: EXTENSION_ID,
          releaseHash: validFixture.releaseHash,
          bytes: validFixture.archive,
        },
        {
          extensionId: CORRUPT_EXTENSION_ID,
          releaseHash: corruptFixture.releaseHash,
          bytes: corruptArchive,
        },
      ],
      failures: [
        {
          extensionId: FAILED_EXTENSION_ID,
          releaseHash: failedFixture.releaseHash,
          message: "The Server could not download this package.",
        },
      ],
    });

    const valid = entries.find((item) => item.entry.id === EXTENSION_ID);
    const reportedFailure = entries.find((item) => item.entry.id === FAILED_EXTENSION_ID);
    const corrupt = entries.find((item) => item.entry.id === CORRUPT_EXTENSION_ID);
    assert.equal(valid?.available, true);
    assert.equal(valid?.entry.validation, "valid");
    assert.equal(reportedFailure?.available, false);
    assert.equal(reportedFailure?.entry.validation, "invalid");
    assert.equal(
      reportedFailure?.entry.errors[0]?.message,
      "The Server could not download this package.",
    );
    assert.equal(corrupt?.available, false);
    assert.equal(corrupt?.entry.validation, "invalid");
    assert.equal(await store.has(ORIGIN, EXTENSION_ID, validFixture.releaseHash), true);
  } finally {
    await scratch.dispose();
  }
});

async function writeProjection(directory: string, manifest: ExtensionManifest): Promise<void> {
  await mkdir(join(directory, "desktop"), { recursive: true });
  await writeFile(join(directory, "manifest.json"), JSON.stringify(manifest, null, 2));
  await writeFile(join(directory, "desktop/index.html"), ENTRY);
}

test("cache stores a release keyed by its manifest digest independently from its ZIP hash", async () => {
  const scratch = await createScratchDirectory("server-package-store");
  try {
    const fixture = await createReleaseFixture(scratch.path, "release.zip");
    const release = {
      id: EXTENSION_ID,
      name: "calendar",
      displayName: "Calendar",
      version: "1.0.0",
      releaseHash: fixture.releaseHash,
      packageHash: fixture.packageHash,
      manifest: fixture.manifest,
    };
    const store = new ServerExtensionPackageStore(join(scratch.path, "cache"), APP_VERSION);

    const entry = await store.cache(ORIGIN, release, fixture.archive);

    assert.notEqual(release.releaseHash, release.packageHash);
    assert.equal(entry.source, "server");
    assert.equal(entry.id, EXTENSION_ID);
    assert.equal(entry.instanceId, store.instanceId(ORIGIN, EXTENSION_ID));
    assert.equal(entry.origin?.serverId, ORIGIN.serverId);
    assert.equal(entry.releaseHash, release.releaseHash);
    assert.equal(await store.has(ORIGIN, EXTENSION_ID, release.releaseHash), true);
    assert.equal(await readFile(join(entry.directory, "desktop/index.html"), "utf8"), ENTRY);
  } finally {
    await scratch.dispose();
  }
});

test("cache reuses verified content when a repacked ZIP has different metadata", async () => {
  const scratch = await createScratchDirectory("server-package-store-repacked");
  try {
    const compressed = await createReleaseFixture(scratch.path, "compressed.zip", true);
    const stored = await createReleaseFixture(scratch.path, "stored.zip", false);
    const release = {
      id: EXTENSION_ID,
      name: "calendar",
      displayName: "Calendar",
      version: "1.0.0",
      releaseHash: compressed.releaseHash,
      packageHash: compressed.packageHash,
      manifest: compressed.manifest,
    };
    const store = new ServerExtensionPackageStore(join(scratch.path, "cache"), APP_VERSION);

    const cached = await store.cache(ORIGIN, release, compressed.archive);
    const repacked = await store.cache(
      ORIGIN,
      { ...release, packageHash: stored.packageHash },
      stored.archive,
    );

    assert.equal(compressed.releaseHash, stored.releaseHash);
    assert.notEqual(compressed.packageHash, stored.packageHash);
    assert.equal(repacked.directory, cached.directory);
    assert.equal(repacked.releaseHash, cached.releaseHash);
  } finally {
    await scratch.dispose();
  }
});

test("cache validates the downloaded ZIP even when its content is already cached", async () => {
  const scratch = await createScratchDirectory("server-package-store-cached-hash");
  try {
    const fixture = await createReleaseFixture(scratch.path, "release.zip");
    const release = {
      id: EXTENSION_ID,
      name: "calendar",
      displayName: "Calendar",
      version: "1.0.0",
      releaseHash: fixture.releaseHash,
      packageHash: fixture.packageHash,
      manifest: fixture.manifest,
    };
    const store = new ServerExtensionPackageStore(join(scratch.path, "cache"), APP_VERSION);
    await store.cache(ORIGIN, release, fixture.archive);

    await assert.rejects(() => store.cache(ORIGIN, release, new Uint8Array([1])), /catalog hash/u);
  } finally {
    await scratch.dispose();
  }
});

test("a corrupt cached release is removed so the next sync can redownload it", async () => {
  const scratch = await createScratchDirectory("server-package-store-tampered");
  try {
    const fixture = await createReleaseFixture(scratch.path, "release.zip");
    const release = {
      id: EXTENSION_ID,
      name: "calendar",
      displayName: "Calendar",
      version: "1.0.0",
      releaseHash: fixture.releaseHash,
      packageHash: fixture.packageHash,
      manifest: fixture.manifest,
    };
    const store = new ServerExtensionPackageStore(join(scratch.path, "cache"), APP_VERSION);
    const cached = await store.cache(ORIGIN, release, fixture.archive);

    await writeFile(
      join(cached.directory, "desktop/index.html"),
      "<!doctype html><main>Tampered</main>",
    );

    assert.equal(await store.has(ORIGIN, EXTENSION_ID, fixture.releaseHash), false);

    const restored = await store.cache(ORIGIN, release, fixture.archive);

    assert.equal(restored.validation, "valid");
    assert.equal(await store.has(ORIGIN, EXTENSION_ID, fixture.releaseHash), true);
  } finally {
    await scratch.dispose();
  }
});

test("cache rejects a manifest whose bytes do not match the catalog release hash", async () => {
  const scratch = await createScratchDirectory("server-package-store-manifest-hash");
  try {
    const fixture = await createReleaseFixture(scratch.path, "release.zip");
    const release = {
      id: EXTENSION_ID,
      name: "calendar",
      displayName: "Calendar",
      version: "1.0.0",
      releaseHash: "0".repeat(64),
      packageHash: fixture.packageHash,
      manifest: fixture.manifest,
    };
    const store = new ServerExtensionPackageStore(join(scratch.path, "cache"), APP_VERSION);

    await assert.rejects(() => store.cache(ORIGIN, release, fixture.archive), /manifest digest/u);
  } finally {
    await scratch.dispose();
  }
});

test("cache rejects an archive whose package hash does not match the catalog", async () => {
  const scratch = await createScratchDirectory("server-package-store-hash");
  try {
    const store = new ServerExtensionPackageStore(join(scratch.path, "cache"), APP_VERSION);
    const release = {
      id: EXTENSION_ID,
      name: "calendar",
      displayName: "Calendar",
      version: "1.0.0",
      releaseHash: "a".repeat(64),
      packageHash: "0".repeat(64),
      manifest: {
        manifestVersion: 2,
        id: EXTENSION_ID,
        name: "calendar",
        displayName: "Calendar",
        version: "1.0.0",
        targets: 2,
        files: {},
        desktop: { apiVersion: 1 },
      },
    };

    await assert.rejects(() => store.cache(ORIGIN, release, new Uint8Array([1])));
  } finally {
    await scratch.dispose();
  }
});

test("cached release content is shared across Server users", async () => {
  const scratch = await createScratchDirectory("server-package-store-shared");
  try {
    const fixture = await createReleaseFixture(scratch.path, "release.zip");
    const release = {
      id: EXTENSION_ID,
      name: "calendar",
      displayName: "Calendar",
      version: "1.0.0",
      releaseHash: fixture.releaseHash,
      packageHash: fixture.packageHash,
      manifest: fixture.manifest,
    };
    const otherOrigin = { ...ORIGIN, userId: "user-b" };
    const store = new ServerExtensionPackageStore(join(scratch.path, "cache"), APP_VERSION);

    await store.cache(ORIGIN, release, fixture.archive);

    assert.equal(await store.has(otherOrigin, EXTENSION_ID, fixture.releaseHash), true);
    const entry = await store.load(otherOrigin, EXTENSION_ID, fixture.releaseHash);
    assert.equal(entry.origin.userId, "user-b");
    assert.notEqual(entry.instanceId, store.instanceId(ORIGIN, EXTENSION_ID));
  } finally {
    await scratch.dispose();
  }
});

test("cache accepts the checksummed Desktop projection with the original manifest", async () => {
  const scratch = await createScratchDirectory("server-package-store-projection");
  try {
    const fixture = await createReleaseFixture(scratch.path, "release.zip");
    const projectedDirectory = join(scratch.path, "projected", EXTENSION_ID);
    await writeProjection(projectedDirectory, fixture.manifest);
    const archive = await createArchive(projectedDirectory, join(scratch.path, "projected.zip"));
    const release = {
      id: EXTENSION_ID,
      name: "calendar",
      displayName: "Calendar",
      version: "1.0.0",
      releaseHash: fixture.releaseHash,
      packageHash: createHash("sha256").update(archive).digest("hex"),
      manifest: fixture.manifest,
    };
    const store = new ServerExtensionPackageStore(join(scratch.path, "cache"), APP_VERSION);

    const entry = await store.cache(ORIGIN, release, archive);

    assert.equal(entry.validation, "valid");
    assert.equal(entry.releaseHash, fixture.releaseHash);
    assert.deepEqual(
      JSON.parse(await readFile(join(entry.directory, "manifest.json"), "utf8")),
      fixture.manifest,
    );
  } finally {
    await scratch.dispose();
  }
});

test("Server packages reject Desktop contributions outside the desktop directory", async () => {
  const scratch = await createScratchDirectory("server-package-store-desktop-path");
  try {
    const fixture = await createReleaseFixture(scratch.path, "full.zip");
    const manifest = structuredClone(fixture.manifest);
    manifest.desktop!.contributes!.mainViews![0].entry = "index.html";
    const source = join(scratch.path, "invalid", EXTENSION_ID);
    await writeTestExtension(source, manifest, {
      "background.js": BACKGROUND,
      "desktop/index.html": ENTRY,
      "icon.svg": TEST_ICON_SVG,
      "index.html": ROOT_ENTRY,
      "server/Calendar.Extension.dll": SERVER_ENTRY,
    });
    const manifestBytes = await readFile(join(source, "manifest.json"));
    const archive = await createArchive(source, join(scratch.path, "invalid.zip"));
    const release = {
      id: EXTENSION_ID,
      name: "calendar",
      displayName: "Calendar",
      version: "1.0.0",
      releaseHash: createHash("sha256").update(manifestBytes).digest("hex"),
      packageHash: createHash("sha256").update(archive).digest("hex"),
      manifest,
    };
    const store = new ServerExtensionPackageStore(join(scratch.path, "cache"), APP_VERSION);

    await assert.rejects(
      () => store.cache(ORIGIN, release, archive),
      /must be under the desktop directory/u,
    );
  } finally {
    await scratch.dispose();
  }
});

test("package pruning retains referenced releases and leaves user data intact", async () => {
  const scratch = await createScratchDirectory("server-package-store-prune");
  try {
    const fixture = await createReleaseFixture(scratch.path, "release.zip");
    const release = {
      id: EXTENSION_ID,
      name: "calendar",
      displayName: "Calendar",
      version: "1.0.0",
      releaseHash: fixture.releaseHash,
      packageHash: fixture.packageHash,
      manifest: fixture.manifest,
    };
    const cacheDirectory = join(scratch.path, "cache");
    const userData = join(scratch.path, "extensions", "data", EXTENSION_ID, "keep.txt");
    const store = new ServerExtensionPackageStore(cacheDirectory, APP_VERSION);
    await store.cache(ORIGIN, release, fixture.archive);
    await mkdir(join(scratch.path, "extensions", "data", EXTENSION_ID), { recursive: true });
    await writeFile(userData, "keep");

    await mkdir(join(cacheDirectory, ".staging", "interrupted"), { recursive: true });
    await writeFile(join(cacheDirectory, ".staging", "interrupted", "partial.zip"), "partial");
    await store.initialize();
    assert.equal((await readdir(cacheDirectory)).includes(".staging"), false);
    await store.prune(new Set([release.releaseHash]));
    assert.equal(await store.has(ORIGIN, EXTENSION_ID, release.releaseHash), true);
    await store.prune(new Set());
    assert.equal(await store.has(ORIGIN, EXTENSION_ID, release.releaseHash), false);
    assert.equal(await readFile(userData, "utf8"), "keep");
  } finally {
    await scratch.dispose();
  }
});
