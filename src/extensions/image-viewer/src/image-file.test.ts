import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

import type { ExtensionManifest } from "@agw/extension-sdk";

import { formatFileSize, imageMimeType, sameBytes } from "./image-file";

test("imageMimeType_EveryManifestPattern_HasAnImageType", async () => {
  const manifest = JSON.parse(
    await readFile(new URL("../manifest.json", import.meta.url), "utf8"),
  ) as ExtensionManifest;
  const patterns = manifest.contributes!.fileViewers!.flatMap((viewer) => viewer.patterns);
  assert.ok(patterns.length > 0);
  for (const pattern of patterns) {
    assert.match(pattern, /^\*\.[a-z]+$/u);
    assert.match(imageMimeType(`picture${pattern.slice(1)}`), /^image\//u, pattern);
  }
});

test("imageMimeType_KnownExtensions_IgnoreCase", () => {
  assert.equal(imageMimeType("photo.JPG"), "image/jpeg");
  assert.equal(imageMimeType("logo.Svg"), "image/svg+xml");
  assert.equal(imageMimeType("archive.tar.png"), "image/png");
});

test("imageMimeType_UnknownExtension_Throws", () => {
  assert.throws(() => imageMimeType("notes.txt"), /notes\.txt is not a supported image file/u);
  assert.throws(() => imageMimeType("png"), /png is not a supported image file/u);
});

test("formatFileSize_EachUnit_MatchesTheFilesTree", () => {
  assert.equal(formatFileSize(0), "0 B");
  assert.equal(formatFileSize(1023), "1023 B");
  assert.equal(formatFileSize(1024), "1.0 KB");
  assert.equal(formatFileSize(220_058), "214.9 KB");
  assert.equal(formatFileSize(5 * 1024 * 1024 + 1024 * 100), "5.1 MB");
});

test("sameBytes_ComparesLengthAndEveryByte", () => {
  assert.equal(sameBytes(new Uint8Array([1, 2, 3]), new Uint8Array([1, 2, 3])), true);
  assert.equal(sameBytes(new Uint8Array([1, 2, 3]), new Uint8Array([1, 2, 4])), false);
  assert.equal(sameBytes(new Uint8Array([1, 2]), new Uint8Array([1, 2, 3])), false);
  assert.equal(sameBytes(new Uint8Array(), new Uint8Array()), true);
});
