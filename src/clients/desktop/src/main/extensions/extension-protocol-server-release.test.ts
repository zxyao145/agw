import assert from "node:assert/strict";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { evaluateExtensionManifest } from "../../shared/extension-manifest";
import { serverExtensionOriginUrl, serverExtensionReleaseUrl } from "../../shared/extension-urls";
import { createScratchDirectory } from "../scratch-directory.test-helper";
import { createExtensionProtocolHandler, type ExtensionProtocolState } from "./extension-protocol";
import { testManifest, writeTestExtension } from "./extension-fixture.test-helper";

const EXTENSION_ID = "01a173c6-4934-746b-b76c-47e2e269b572";
const INSTANCE_ID = "server-" + "a".repeat(64);
const FIRST_RELEASE = "b".repeat(64);
const SECOND_RELEASE = "c".repeat(64);

async function fetchFile(fileUrl: string): Promise<Response> {
  return new Response(await readFile(fileURLToPath(fileUrl)));
}

test("server release URLs share an origin while serving their pinned files", async () => {
  const scratch = await createScratchDirectory("protocol-server-release");
  try {
    const releases = new Map<string, ExtensionProtocolState>();
    for (const [releaseHash, version, contents] of [
      [FIRST_RELEASE, "1.0.0", "first release"],
      [SECOND_RELEASE, "2.0.0", "second release"],
    ] as const) {
      const manifest = { ...testManifest(EXTENSION_ID), version };
      const rootDirectory = join(scratch.path, releaseHash, EXTENSION_ID);
      await writeTestExtension(rootDirectory, manifest, { "desktop/index.html": contents });
      releases.set(releaseHash, {
        manifest: evaluateExtensionManifest(manifest, "0.5.0").manifest!,
        rootDirectory,
        enabled: true,
        grantedHostPermissions: [],
      });
    }

    const handler = createExtensionProtocolHandler({
      extensionId: INSTANCE_ID,
      getState: (releaseHash: string | null) =>
        releaseHash ? (releases.get(releaseHash) ?? null) : null,
      sdkDirectory: scratch.path,
      fetchFile,
    });
    const origin = serverExtensionOriginUrl(INSTANCE_ID);
    const firstUrl = new URL(
      serverExtensionReleaseUrl(INSTANCE_ID, EXTENSION_ID, FIRST_RELEASE, "desktop/index.html"),
    );
    const secondUrl = new URL(
      serverExtensionReleaseUrl(INSTANCE_ID, EXTENSION_ID, SECOND_RELEASE, "desktop/index.html"),
    );

    const first = await handler(new Request(firstUrl));
    const second = await handler(new Request(secondUrl));

    assert.equal(firstUrl.protocol, "agw-extension:");
    assert.equal(firstUrl.host, secondUrl.host);
    assert.equal(firstUrl.host, INSTANCE_ID);
    assert.equal(first.status, 200);
    assert.equal(await first.text(), "first release");
    assert.equal(second.status, 200);
    assert.equal(await second.text(), "second release");
    assert.equal(serverExtensionOriginUrl(INSTANCE_ID), origin);
  } finally {
    await scratch.dispose();
  }
});

test("a Server release serves its reserved background page", async () => {
  const scratch = await createScratchDirectory("protocol-server-background");
  try {
    const manifest = evaluateExtensionManifest(testManifest(EXTENSION_ID), "0.5.0").manifest!;
    const rootDirectory = join(scratch.path, FIRST_RELEASE, EXTENSION_ID);
    await writeTestExtension(rootDirectory, manifest);
    const handler = createExtensionProtocolHandler({
      extensionId: INSTANCE_ID,
      getState: (releaseHash: string | null) =>
        releaseHash === FIRST_RELEASE
          ? { manifest, rootDirectory, enabled: true, grantedHostPermissions: [] }
          : null,
      sdkDirectory: scratch.path,
      fetchFile,
    });
    const backgroundUrl = new URL(
      `agw-extension://${INSTANCE_ID}/__agw/releases/${FIRST_RELEASE}/${EXTENSION_ID}/__agw/background.html`,
    );

    const response = await handler(new Request(backgroundUrl));

    assert.equal(response.status, 200);
    assert.match(await response.text(), new RegExp(FIRST_RELEASE, "u"));
  } finally {
    await scratch.dispose();
  }
});

test("prefix-less reserved requests resolve to the referring Server release", async () => {
  const scratch = await createScratchDirectory("protocol-server-referrer");
  try {
    const manifest = evaluateExtensionManifest(testManifest(EXTENSION_ID), "0.5.0").manifest!;
    const rootDirectory = join(scratch.path, FIRST_RELEASE, EXTENSION_ID);
    await writeTestExtension(rootDirectory, manifest);
    await mkdir(join(scratch.path, "v1"), { recursive: true });
    await writeFile(join(scratch.path, "v1", "sdk.js"), "export {};");
    const requestedReleases: (string | null)[] = [];
    const requestedFiles: string[] = [];
    const handler = createExtensionProtocolHandler({
      extensionId: INSTANCE_ID,
      getState: (releaseHash: string | null) => {
        requestedReleases.push(releaseHash);
        return releaseHash === FIRST_RELEASE
          ? { manifest, rootDirectory, enabled: true, grantedHostPermissions: [] }
          : null;
      },
      sdkDirectory: scratch.path,
      fetchFile: async (fileUrl) => {
        requestedFiles.push(fileURLToPath(fileUrl));
        return fetchFile(fileUrl);
      },
    });
    const referrer = serverExtensionReleaseUrl(
      INSTANCE_ID,
      EXTENSION_ID,
      FIRST_RELEASE,
      "desktop/index.html",
    );

    const response = await handler(
      new Request(new URL("__agw/sdk.js", serverExtensionOriginUrl(INSTANCE_ID)), {
        headers: { referer: referrer },
      }),
    );

    assert.deepEqual(requestedReleases, [FIRST_RELEASE]);
    assert.deepEqual(requestedFiles, [join(scratch.path, "v1", "sdk.js")]);
    assert.equal(response.status, 200);
  } finally {
    await scratch.dispose();
  }
});

test("server release URLs reject paths outside the package root", () => {
  assert.throws(() =>
    serverExtensionReleaseUrl(INSTANCE_ID, EXTENSION_ID, FIRST_RELEASE, "../secret.txt"),
  );
});
