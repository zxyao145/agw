import assert from "node:assert/strict";
import { mkdir, readFile, rm, symlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";

import { createScratchDirectory } from "../scratch-directory.test-helper";
import {
  MAX_ICON_BYTES,
  mergeExtensionCatalogs,
  scanExtensionDirectory,
  toInstalledExtension,
} from "./extension-catalog";
import { testManifest, writeTestExtension } from "./extension-fixture.test-helper";

const APP_VERSION = "0.5.0";

test("scanning lists valid extensions with icon data URLs matching the files", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(join(scratch.path, "hello-world"), testManifest("hello-world"));
    await mkdir(join(scratch.path, ".staging", "abc"), { recursive: true });
    await writeFile(join(scratch.path, "notes.txt"), "not an extension");

    const entries = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");

    assert.deepEqual(
      entries.map((entry) => [entry.id, entry.validation]),
      [["hello-world", "valid"]],
    );
    const dataUrl = entries[0].icons["icon.svg"];
    assert.match(dataUrl, /^data:image\/svg\+xml;base64,/u);
    assert.deepEqual(
      Buffer.from(dataUrl.split(",")[1], "base64"),
      await readFile(join(scratch.path, "hello-world", "icon.svg")),
    );
  } finally {
    await scratch.dispose();
  }
});

test("a directory name that differs from the manifest id is an error", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(join(scratch.path, "renamed"), testManifest("hello-world"));
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.equal(entry.id, "renamed");
    assert.equal(entry.validation, "invalid");
    assert.deepEqual(
      entry.errors.map((error) => error.code),
      ["DIRECTORY_NAME_MISMATCH"],
    );
  } finally {
    await scratch.dispose();
  }
});

test("a manifest that is not valid JSON has a null manifest", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(join(scratch.path, "broken"), "{ not json");
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.equal(entry.manifest, null);
    assert.equal(entry.validation, "invalid");
    assert.equal(entry.errors[0].code, "MANIFEST_PARSE_FAILED");
  } finally {
    await scratch.dispose();
  }
});

test("missing package files and missing manifests are errors", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(
      join(scratch.path, "missing-entry"),
      testManifest("missing-entry", {
        contributes: {
          mainViews: [{ id: "app", title: "App", icon: "Box", entry: "missing.html" }],
        },
      }),
    );
    await mkdir(join(scratch.path, "empty"));
    const entries = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.deepEqual(
      entries.map((entry) => [entry.id, entry.errors.map((error) => [error.code, error.path])]),
      [
        ["empty", [["MANIFEST_NOT_FOUND", null]]],
        ["missing-entry", [["PACKAGE_FILE_NOT_FOUND", "contributes.mainViews[0].entry"]]],
      ],
    );
  } finally {
    await scratch.dispose();
  }
});

test("symbolic links that resolve outside the root are errors", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeFile(join(scratch.path, "outside.js"), "export {};");
    const directory = join(scratch.path, "extensions", "linked");
    await writeTestExtension(directory, testManifest("linked"));
    await rm(join(directory, "background.js"));
    await symlink(join(scratch.path, "outside.js"), join(directory, "background.js"));

    const [entry] = await scanExtensionDirectory(
      join(scratch.path, "extensions"),
      APP_VERSION,
      "user",
    );
    assert.equal(entry.validation, "invalid");
    assert.deepEqual(
      entry.errors.map((error) => [error.code, error.path]),
      [["PACKAGE_PATH_OUTSIDE_ROOT", "background"]],
    );
  } finally {
    await scratch.dispose();
  }
});

test("icons larger than 256 KB are errors", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(join(scratch.path, "big-icon"), testManifest("big-icon"), {
      "icon.svg": `<svg xmlns="http://www.w3.org/2000/svg"><!--${"x".repeat(MAX_ICON_BYTES)}--></svg>`,
    });
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.equal(entry.validation, "invalid");
    assert.deepEqual(
      entry.errors.map((error) => [error.code, error.path]),
      [
        ["ICON_TOO_LARGE", "icon"],
        ["ICON_TOO_LARGE", "contributes.ribbon[0].icon"],
      ],
    );
  } finally {
    await scratch.dispose();
  }
});

test("incompatible versions keep the manifest and report the reason", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(
      join(scratch.path, "future"),
      testManifest("future", { minAppVersion: "9.0.0" }),
    );
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.equal(entry.validation, "incompatible");
    assert.equal(entry.manifest?.id, "future");
    assert.equal(entry.errors[0].code, "APP_VERSION_TOO_OLD");
  } finally {
    await scratch.dispose();
  }
});

test("installed extensions report when the declaration exceeds the approved grants", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    await writeTestExtension(join(scratch.path, "grants"), testManifest("grants"));
    const [entry] = await scanExtensionDirectory(scratch.path, APP_VERSION, "user");
    assert.equal(toInstalledExtension(entry, undefined).permissionApprovalRequired, false);
    assert.equal(
      toInstalledExtension(entry, {
        enabled: false,
        grantedPermissions: [],
        grantedHostPermissions: ["https://api.example.com"],
        lastActivatedVersion: "0.9.0",
      }).permissionApprovalRequired,
      true,
    );
    const approved = toInstalledExtension(entry, {
      enabled: true,
      grantedPermissions: ["context.project"],
      grantedHostPermissions: ["https://api.example.com"],
      lastActivatedVersion: null,
    });
    assert.equal(approved.permissionApprovalRequired, false);
    assert.equal(approved.enabled, true);
  } finally {
    await scratch.dispose();
  }
});

test("bundled and user directories merge with user copies winning and uninstalled bundled ones left out", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    const bundledDirectory = join(scratch.path, "bundled");
    const userDirectory = join(scratch.path, "user");
    for (const id of ["shared", "kept", "removed"]) {
      await writeTestExtension(join(bundledDirectory, id), testManifest(id));
    }
    await writeTestExtension(join(userDirectory, "shared"), testManifest("shared"));
    await writeTestExtension(join(userDirectory, "added"), testManifest("added"));

    const merged = mergeExtensionCatalogs(
      await scanExtensionDirectory(bundledDirectory, APP_VERSION, "bundled"),
      await scanExtensionDirectory(userDirectory, APP_VERSION, "user"),
      ["removed"],
    );

    assert.deepEqual(
      merged.map((entry) => [entry.id, entry.source, entry.directory]),
      [
        ["added", "user", join(userDirectory, "added")],
        ["kept", "bundled", join(bundledDirectory, "kept")],
        ["shared", "user", join(userDirectory, "shared")],
      ],
    );
    assert.equal(toInstalledExtension(merged[1], undefined).source, "bundled");

    // 卸载用户副本后内置副本重新出现。Removing the user copy brings the bundled copy back.
    await rm(join(userDirectory, "shared"), { recursive: true });
    const afterUninstall = mergeExtensionCatalogs(
      await scanExtensionDirectory(bundledDirectory, APP_VERSION, "bundled"),
      await scanExtensionDirectory(userDirectory, APP_VERSION, "user"),
      ["removed"],
    );
    assert.equal(
      afterUninstall.find((entry) => entry.id === "shared")?.directory,
      join(bundledDirectory, "shared"),
    );
  } finally {
    await scratch.dispose();
  }
});

test("a missing bundled directory scans as empty", async () => {
  const scratch = await createScratchDirectory("catalog");
  try {
    assert.deepEqual(
      await scanExtensionDirectory(join(scratch.path, "absent"), APP_VERSION, "bundled"),
      [],
    );
  } finally {
    await scratch.dispose();
  }
});
