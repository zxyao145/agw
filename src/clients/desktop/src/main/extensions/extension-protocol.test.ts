import assert from "node:assert/strict";
import { mkdir, readFile, symlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { evaluateExtensionManifest } from "../../shared/extension-manifest";
import { createScratchDirectory } from "../scratch-directory.test-helper";
import { createExtensionProtocolHandler, type ExtensionProtocolState } from "./extension-protocol";
import { testManifest, writeTestExtension } from "./extension-fixture.test-helper";

const EXTENSION_ID = "01a173c6-4934-746b-b76c-47e2e269b572";
const ORIGIN = `agw-extension://${EXTENSION_ID}`;

async function nodeFetchFile(fileUrl: string): Promise<Response> {
  return new Response(await readFile(fileURLToPath(fileUrl)));
}

async function createHandler(enabled = true) {
  const scratch = await createScratchDirectory("protocol");
  const root = join(scratch.path, "extensions", EXTENSION_ID);
  const sdk = join(scratch.path, "extension-sdk");
  await writeTestExtension(root, testManifest(EXTENSION_ID), {
    "app.wasm": new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0]),
    "lib/app.mjs": "export const value = 1;",
    "assets/logo.png": new Uint8Array([137, 80, 78, 71]),
  });
  await mkdir(join(sdk, "v1"), { recursive: true });
  await writeFile(join(sdk, "v1", "sdk.js"), "export const sdkVersion = 1;");
  await writeFile(join(sdk, "v1", "sdk.d.ts"), "export declare const sdkVersion: 1;");
  await writeFile(join(sdk, "v1", "manifest.schema.json"), "{}");
  await writeFile(join(sdk, "v1", "typography.css"), ".agw-prose { color: inherit; }");
  await writeFile(join(sdk, "v1", "theme-defaults.css"), ":root { --spacing: 0.25rem; }");
  await writeFile(join(scratch.path, "secret.txt"), "outside");
  await symlink(join(scratch.path, "secret.txt"), join(root, "leak.txt"));

  const state: { current: ExtensionProtocolState | null } = {
    current: {
      manifest: evaluateExtensionManifest(testManifest(EXTENSION_ID), "0.5.0").manifest!,
      rootDirectory: root,
      enabled,
      grantedHostPermissions: ["https://api.example.com"],
    },
  };
  const handler = createExtensionProtocolHandler({
    extensionId: EXTENSION_ID,
    getState: () => state.current,
    sdkDirectory: sdk,
    fetchFile: nodeFetchFile,
  });
  return { handler, state, dispose: scratch.dispose };
}

const get = (url: string) => new Request(url);

test("requests for another extension's host are not found", async () => {
  const { handler, dispose } = await createHandler();
  try {
    assert.equal((await handler(get("agw-extension://other/index.html"))).status, 404);
    assert.equal((await handler(get(`${ORIGIN}/index.html`))).status, 200);
  } finally {
    await dispose();
  }
});

test("package files are not served while the extension is disabled", async () => {
  const { handler, dispose } = await createHandler(false);
  try {
    assert.equal((await handler(get(`${ORIGIN}/index.html`))).status, 404);
    assert.equal((await handler(get(`${ORIGIN}/__agw/sdk.js`))).status, 200);
  } finally {
    await dispose();
  }
});

test("paths outside the package and symbolic links leaving it are not found", async () => {
  const { handler, dispose } = await createHandler();
  try {
    for (const path of [
      "/../secret.txt",
      "/%2e%2e/secret.txt",
      "/lib/%2e%2e/%2e%2e/secret.txt",
      "/leak.txt",
      "/lib",
      "/lib/",
      "/missing.html",
      "/index.html/child",
      "/__agw/other.js",
      "/__agw/other.css",
      "/",
    ]) {
      assert.equal((await handler(get(`${ORIGIN}${path}`))).status, 404, path);
    }
  } finally {
    await dispose();
  }
});

test("the SDK, its types and the schema are served for the manifest's API version", async () => {
  const { handler, dispose } = await createHandler();
  try {
    const sdk = await handler(get(`${ORIGIN}/__agw/sdk.js`));
    assert.equal(sdk.headers.get("Content-Type"), "text/javascript");
    assert.equal(await sdk.text(), "export const sdkVersion = 1;");
    const types = await handler(get(`${ORIGIN}/__agw/sdk.d.ts`));
    assert.equal(await types.text(), "export declare const sdkVersion: 1;");
    const schema = await handler(get(`${ORIGIN}/__agw/manifest.schema.json`));
    assert.equal(schema.headers.get("Content-Type"), "application/json");
  } finally {
    await dispose();
  }
});

test("the shared typography stylesheet is served as CSS", async () => {
  const { handler, dispose } = await createHandler();
  try {
    const response = await handler(get(`${ORIGIN}/__agw/typography.css`));
    assert.equal(response.status, 200);
    assert.equal(response.headers.get("Content-Type"), "text/css");
    assert.equal(await response.text(), ".agw-prose { color: inherit; }");
  } finally {
    await dispose();
  }
});

test("the default variable stylesheet is served as CSS", async () => {
  const { handler, dispose } = await createHandler();
  try {
    const response = await handler(get(`${ORIGIN}/__agw/theme-defaults.css`));
    assert.equal(response.status, 200);
    assert.equal(response.headers.get("Content-Type"), "text/css");
    assert.equal(await response.text(), ":root { --spacing: 0.25rem; }");
  } finally {
    await dispose();
  }
});

test("background.html loads the declared background script as a module", async () => {
  const { handler, state, dispose } = await createHandler();
  try {
    const response = await handler(get(`${ORIGIN}/__agw/background.html`));
    assert.equal(response.headers.get("Content-Type"), "text/html");
    assert.equal(
      await response.text(),
      '<!doctype html><meta charset="utf-8"><script type="module" src="/background.js"></script>',
    );
    delete state.current!.manifest.background;
    assert.equal((await handler(get(`${ORIGIN}/__agw/background.html`))).status, 404);
  } finally {
    await dispose();
  }
});

test("content types follow the file extension", async () => {
  const { handler, dispose } = await createHandler();
  try {
    const types: [string, string][] = [
      ["/app.wasm", "application/wasm"],
      ["/background.js", "text/javascript"],
      ["/lib/app.mjs", "text/javascript"],
      ["/index.html", "text/html"],
      ["/assets/logo.png", "image/png"],
      ["/icon.svg", "image/svg+xml"],
    ];
    for (const [path, type] of types) {
      assert.equal(
        (await handler(get(`${ORIGIN}${path}`))).headers.get("Content-Type"),
        type,
        path,
      );
    }
    const wasm = await handler(get(`${ORIGIN}/app.wasm`));
    assert.deepEqual(
      new Uint8Array(await wasm.arrayBuffer()),
      new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0]),
    );
  } finally {
    await dispose();
  }
});

test("responses carry the CSP, nosniff and no-cache headers", async () => {
  const { handler, dispose } = await createHandler();
  try {
    const response = await handler(get(`${ORIGIN}/index.html`));
    assert.match(response.headers.get("Content-Security-Policy")!, /^default-src 'none'; /u);
    assert.match(
      response.headers.get("Content-Security-Policy")!,
      /connect-src 'self' https:\/\/api\.example\.com/u,
    );
    assert.equal(response.headers.get("X-Content-Type-Options"), "nosniff");
    assert.equal(response.headers.get("Cache-Control"), "no-cache");
  } finally {
    await dispose();
  }
});

test("an extension without a valid manifest serves nothing", async () => {
  const { handler, state, dispose } = await createHandler();
  try {
    state.current = null;
    assert.equal((await handler(get(`${ORIGIN}/__agw/sdk.js`))).status, 404);
    assert.equal((await handler(get(`${ORIGIN}/index.html`))).status, 404);
  } finally {
    await dispose();
  }
});
