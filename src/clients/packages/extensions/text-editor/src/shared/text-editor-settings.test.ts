import assert from "node:assert/strict";
import { after, beforeEach, test } from "node:test";

import { createExtensionError } from "@agw/extension-sdk";

import { connectTestHost } from "../test-host";
import {
  DEFAULT_TEXT_EDITOR_SETTINGS,
  readTextEditorSettings,
  resolveTextEditorFontSizes,
  TEXT_EDITOR_FONT_SIZES,
  TextEditorSettingsError,
  writeTextEditorSettings,
  type TextEditorSettings,
} from "./text-editor-settings";

const SETTINGS_URI = "file:///storage/text-editor/settings.json";

// 宿主端的文件系统，键为 URI。The host-side file system, keyed by URI.
const files = new Map<string, Uint8Array>();
const requests: string[] = [];

const host = connectTestHost((method, params) => {
  requests.push(method);
  const { uri } = params as { uri: string };
  if (method === "fs.readFile") {
    const data = files.get(uri);
    if (data === undefined) throw createExtensionError("ENOENT", `${uri} does not exist.`);
    return data;
  }
  if (method === "fs.writeFile") {
    files.set(uri, (params as { data: Uint8Array }).data);
    return undefined;
  }
  throw new Error(`Unexpected request ${method}.`);
});
after(() => host.close());
beforeEach(() => {
  files.clear();
  requests.length = 0;
});

function putSettings(text: string): void {
  files.set(SETTINGS_URI, new TextEncoder().encode(text));
}

function settings(documentFontSize: number | null): TextEditorSettings {
  return { schemaVersion: 2, documentFontSize };
}

/** 版本 1 的文件内容，另有 Code 的字号。A version 1 file, with Code's size too. */
function settingsV1(documentFontSize: number | null, codeFontSize: number | null) {
  return { schemaVersion: 1, documentFontSize, codeFontSize };
}

test("TEXT_EDITOR_FONT_SIZES lists the integers 12 to 28", () => {
  assert.equal(TEXT_EDITOR_FONT_SIZES.length, 17);
  assert.equal(TEXT_EDITOR_FONT_SIZES[0], 12);
  assert.equal(TEXT_EDITOR_FONT_SIZES.at(-1), 28);
});

test("readTextEditorSettings returns the defaults when the file does not exist", async () => {
  assert.deepEqual(await readTextEditorSettings(host.api), DEFAULT_TEXT_EDITOR_SETTINGS);
  assert.deepEqual(DEFAULT_TEXT_EDITOR_SETTINGS, settings(null));
});

test("readTextEditorSettings returns a copy that callers can change", async () => {
  const first = await readTextEditorSettings(host.api);
  first.documentFontSize = 20;
  assert.deepEqual(DEFAULT_TEXT_EDITOR_SETTINGS, settings(null));
  assert.deepEqual(await readTextEditorSettings(host.api), settings(null));
});

test("readTextEditorSettings requests settings.json under the storage URI", async () => {
  const uris: string[] = [];
  const recording = connectTestHost((_method, params) => {
    uris.push((params as { uri: string }).uri);
    throw createExtensionError("ENOENT", "missing");
  });
  try {
    await readTextEditorSettings(recording.api);
  } finally {
    recording.close();
  }
  assert.deepEqual(uris, ["file:///storage/text-editor/settings.json"]);
});

test("readTextEditorSettings reads the boundary sizes", async () => {
  putSettings(JSON.stringify(settings(12)));
  assert.deepEqual(await readTextEditorSettings(host.api), settings(12));
  putSettings(JSON.stringify(settings(28)));
  assert.deepEqual(await readTextEditorSettings(host.api), settings(28));
});

test("readTextEditorSettings reads a version 1 file without its code size", async () => {
  putSettings(JSON.stringify(settingsV1(18, 15)));
  assert.deepEqual(await readTextEditorSettings(host.api), settings(18));
  putSettings(JSON.stringify(settingsV1(null, null)));
  assert.deepEqual(await readTextEditorSettings(host.api), settings(null));
});

const INVALID: [string, string][] = [
  ["a size below 12", JSON.stringify(settings(11))],
  ["a size above 28", JSON.stringify(settings(29))],
  ["a fractional size", JSON.stringify(settings(16.5))],
  ["an unknown field", JSON.stringify({ ...settings(null), extra: 1 })],
  ["a code size in version 2", JSON.stringify({ ...settings(null), codeFontSize: 14 })],
  ["schemaVersion 3", JSON.stringify({ ...settings(null), schemaVersion: 3 })],
  ["a missing field", JSON.stringify({ schemaVersion: 2 })],
  ["a version 1 code size below 12", JSON.stringify(settingsV1(null, 11))],
  [
    "a version 1 file without its code size",
    JSON.stringify({ schemaVersion: 1, documentFontSize: null }),
  ],
  ["text that is not JSON", "not json"],
];

for (const [name, text] of INVALID) {
  test(`readTextEditorSettings rejects ${name}`, async () => {
    putSettings(text);
    await assert.rejects(readTextEditorSettings(host.api), (error: unknown) => {
      assert.ok(error instanceof TextEditorSettingsError);
      assert.ok(error.message.includes("settings.json"));
      return true;
    });
  });
}

test("readTextEditorSettings rethrows read errors other than ENOENT", async () => {
  const failing = connectTestHost(() => {
    throw createExtensionError("EACCES", "Permission denied.");
  });
  try {
    await assert.rejects(readTextEditorSettings(failing.api), (error: unknown) => {
      assert.ok(!(error instanceof TextEditorSettingsError));
      assert.equal((error as { code?: string }).code, "EACCES");
      return true;
    });
  } finally {
    failing.close();
  }
});

test("writeTextEditorSettings then readTextEditorSettings returns the same object", async () => {
  const value = settings(18);
  await writeTextEditorSettings(host.api, value);
  // 读取时 RPC 会转移这个 buffer，所以先检查文件内容。Reading transfers this buffer, so the content is checked first.
  assert.deepEqual(JSON.parse(new TextDecoder().decode(files.get(SETTINGS_URI))), value);
  assert.deepEqual(await readTextEditorSettings(host.api), value);
});

test("writeTextEditorSettings rejects an invalid object before any fs.writeFile request", async () => {
  await assert.rejects(
    writeTextEditorSettings(host.api, settings(11)),
    (error: unknown) =>
      error instanceof TextEditorSettingsError && error.message.includes("settings.json"),
  );
  assert.ok(!requests.includes("fs.writeFile"));
  assert.equal(files.size, 0);
});

function tokens(base: string): Record<string, string> {
  return { "--agw-font-size-base": base };
}

test("resolveTextEditorFontSizes follows the base size by default", () => {
  assert.deepEqual(resolveTextEditorFontSizes(settings(null), tokens("16px")), { document: 16 });
  assert.deepEqual(resolveTextEditorFontSizes(settings(null), tokens("20px")), { document: 20 });
});

test("resolveTextEditorFontSizes keeps an explicit document size", () => {
  assert.deepEqual(resolveTextEditorFontSizes(settings(18), tokens("16px")), { document: 18 });
});

test("resolveTextEditorFontSizes throws when the host theme has no base size", () => {
  assert.throws(() => resolveTextEditorFontSizes(settings(null), {}), {
    message: "The host theme has no --agw-font-size-base.",
  });
  assert.throws(() => resolveTextEditorFontSizes(settings(18), {}), {
    message: "The host theme has no --agw-font-size-base.",
  });
});

test("resolveTextEditorFontSizes throws on a value that is not px", () => {
  assert.throws(() => resolveTextEditorFontSizes(settings(null), tokens("1rem")), {
    message: 'The host theme --agw-font-size-base is not a px value: "1rem".',
  });
});
