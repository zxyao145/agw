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

function settings(
  documentFontSize: number | null,
  codeFontSize: number | null,
): TextEditorSettings {
  return { schemaVersion: 1, documentFontSize, codeFontSize };
}

test("TEXT_EDITOR_FONT_SIZES lists the integers 14 to 28", () => {
  assert.equal(TEXT_EDITOR_FONT_SIZES.length, 15);
  assert.equal(TEXT_EDITOR_FONT_SIZES[0], 14);
  assert.equal(TEXT_EDITOR_FONT_SIZES.at(-1), 28);
});

test("readTextEditorSettings returns the defaults when the file does not exist", async () => {
  assert.deepEqual(await readTextEditorSettings(host.api), DEFAULT_TEXT_EDITOR_SETTINGS);
  assert.deepEqual(DEFAULT_TEXT_EDITOR_SETTINGS, settings(null, null));
});

test("readTextEditorSettings returns a copy that callers can change", async () => {
  const first = await readTextEditorSettings(host.api);
  first.documentFontSize = 20;
  assert.deepEqual(DEFAULT_TEXT_EDITOR_SETTINGS, settings(null, null));
  assert.deepEqual(await readTextEditorSettings(host.api), settings(null, null));
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
  putSettings(JSON.stringify(settings(14, 28)));
  assert.deepEqual(await readTextEditorSettings(host.api), settings(14, 28));
  putSettings(JSON.stringify(settings(28, null)));
  assert.deepEqual(await readTextEditorSettings(host.api), settings(28, null));
});

const INVALID: [string, string][] = [
  ["a size below 14", JSON.stringify(settings(13, null))],
  ["a code size below 14", JSON.stringify(settings(null, 12))],
  ["a size above 28", JSON.stringify(settings(null, 29))],
  ["a fractional size", JSON.stringify(settings(16.5, null))],
  ["an unknown field", JSON.stringify({ ...settings(null, null), extra: 1 })],
  ["schemaVersion 2", JSON.stringify({ ...settings(null, null), schemaVersion: 2 })],
  ["a missing field", JSON.stringify({ schemaVersion: 1, documentFontSize: null })],
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
  const value = settings(18, 15);
  await writeTextEditorSettings(host.api, value);
  assert.ok(files.has(SETTINGS_URI));
  assert.deepEqual(await readTextEditorSettings(host.api), value);
});

test("writeTextEditorSettings rejects an invalid object before any fs.writeFile request", async () => {
  await assert.rejects(
    writeTextEditorSettings(host.api, settings(13, null)),
    (error: unknown) =>
      error instanceof TextEditorSettingsError && error.message.includes("settings.json"),
  );
  assert.ok(!requests.includes("fs.writeFile"));
  assert.equal(files.size, 0);
});

function tokens(base: string, min: string): Record<string, string> {
  return { "--agw-font-size-base": base, "--agw-font-size-min": min };
}

test("resolveTextEditorFontSizes follows the base size by default", () => {
  assert.deepEqual(resolveTextEditorFontSizes(settings(null, null), tokens("16px", "14px")), {
    document: 16,
    code: 14,
    min: 14,
  });
  assert.deepEqual(resolveTextEditorFontSizes(settings(null, null), tokens("20px", "14px")), {
    document: 20,
    code: 17.5,
    min: 14,
  });
});

test("resolveTextEditorFontSizes derives the code size from the document size", () => {
  assert.deepEqual(resolveTextEditorFontSizes(settings(18, null), tokens("16px", "14px")), {
    document: 18,
    code: 16,
    min: 14,
  });
});

test("resolveTextEditorFontSizes keeps an explicit code size independent of the document size", () => {
  assert.equal(resolveTextEditorFontSizes(settings(null, 15), tokens("16px", "14px")).code, 15);
  assert.equal(resolveTextEditorFontSizes(settings(null, 15), tokens("24px", "14px")).code, 15);
});

test("resolveTextEditorFontSizes raises sizes to the minimum of the theme", () => {
  assert.equal(resolveTextEditorFontSizes(settings(14, null), tokens("16px", "15px")).document, 15);
  assert.equal(resolveTextEditorFontSizes(settings(null, 14), tokens("16px", "15px")).code, 15);
});

test("resolveTextEditorFontSizes throws when the host theme has no minimum size", () => {
  assert.throws(
    () => resolveTextEditorFontSizes(settings(null, null), { "--agw-font-size-base": "16px" }),
    { message: "The host theme has no --agw-font-size-min." },
  );
});

test("resolveTextEditorFontSizes throws when the host theme has no base size", () => {
  assert.throws(
    () => resolveTextEditorFontSizes(settings(null, null), { "--agw-font-size-min": "14px" }),
    { message: "The host theme has no --agw-font-size-base." },
  );
});

test("resolveTextEditorFontSizes throws on a value that is not px", () => {
  assert.throws(() => resolveTextEditorFontSizes(settings(null, null), tokens("1rem", "14px")), {
    message: 'The host theme --agw-font-size-base is not a px value: "1rem".',
  });
});
