import assert from "node:assert/strict";
import test from "node:test";

import {
  checkForDesktopUpdate,
  compareDesktopVersions,
  resolveDesktopUpdateAssetName,
  type DesktopUpdateCheckOptions,
} from "./github-release-updater";

const DEFAULT_OPTIONS: DesktopUpdateCheckOptions = {
  currentVersion: "0.2.0",
  packageFlavor: "full",
  platform: "darwin",
  architecture: "arm64",
  windowsDistribution: "squirrel",
};

function releaseResponse(
  version: string,
  assetNames: string[],
  overrides: Record<string, unknown> = {},
): Response {
  return Response.json({
    tag_name: `v${version}`,
    draft: false,
    prerelease: false,
    published_at: "2026-07-28T12:06:39Z",
    assets: assetNames.map((name) => ({ name })),
    ...overrides,
  });
}

test("compareDesktopVersions follows stable SemVer precedence", () => {
  assert.equal(compareDesktopVersions("0.2.1", "0.2.1"), 0);
  assert.equal(compareDesktopVersions("0.2.0", "0.2.1"), -1);
  assert.equal(compareDesktopVersions("1.0.0", "0.99.99"), 1);
  assert.equal(compareDesktopVersions("0.2.1-preview.2", "0.2.1"), -1);
  assert.equal(compareDesktopVersions("0.3.0-preview.1", "0.2.1"), 1);
  assert.equal(compareDesktopVersions("0.2.1-alpha.2", "0.2.1-beta.1"), -1);
  assert.equal(compareDesktopVersions("0.2.1-beta.2", "0.2.1-beta.10"), -1);
});

test("compareDesktopVersions rejects versions outside the Desktop release format", () => {
  for (const version of ["v0.2.1", "0.2", "01.2.3", "0.2.1-rc.1", "0.2.1-preview"]) {
    assert.throws(() => compareDesktopVersions(version, "0.2.1"), /Invalid Desktop version/u);
  }
});

test("resolveDesktopUpdateAssetName selects the current package shape", () => {
  const cases: Array<{
    options: Partial<DesktopUpdateCheckOptions>;
    expected: string | null;
  }> = [
    {
      options: { platform: "darwin", architecture: "x64", packageFlavor: "full" },
      expected: "Agw-Desktop-0.2.1-full-macos-x64.dmg",
    },
    {
      options: { platform: "darwin", architecture: "arm64", packageFlavor: "client" },
      expected: "Agw-Desktop-0.2.1-client-macos-arm64.dmg",
    },
    {
      options: { platform: "linux", architecture: "x64", packageFlavor: "client" },
      expected: "Agw-Desktop-0.2.1-client-linux-x64.deb",
    },
    {
      options: { platform: "win32", architecture: "x64", packageFlavor: "full" },
      expected: "Agw-Desktop-0.2.1-full-windows-x64-Setup.exe",
    },
    {
      options: {
        platform: "win32",
        architecture: "x64",
        packageFlavor: "client",
        windowsDistribution: "squirrel",
      },
      expected: "Agw-Desktop-0.2.1-client-windows-x64-Setup.exe",
    },
    {
      options: {
        platform: "win32",
        architecture: "x64",
        packageFlavor: "client",
        windowsDistribution: "portable",
      },
      expected: "Agw-Desktop-0.2.1-client-windows-x64-Portable.zip",
    },
    {
      options: { platform: "linux", architecture: "arm64", packageFlavor: "full" },
      expected: null,
    },
    {
      options: { platform: "win32", architecture: "arm64", packageFlavor: "client" },
      expected: null,
    },
  ];

  for (const { options, expected } of cases) {
    assert.equal(
      resolveDesktopUpdateAssetName("0.2.1", { ...DEFAULT_OPTIONS, ...options }),
      expected,
    );
  }
});

test("checkForDesktopUpdate returns the matching latest stable release asset", async () => {
  const assetName = "Agw-Desktop-0.2.1-full-macos-arm64.dmg";
  let requestedUrl = "";
  let requestedInit: RequestInit | undefined;

  const result = await checkForDesktopUpdate(async (input, init) => {
    requestedUrl = String(input);
    requestedInit = init;
    return releaseResponse("0.2.1", [assetName]);
  }, DEFAULT_OPTIONS);

  assert.equal(requestedUrl, "https://api.github.com/repos/zxyao145/agw/releases/latest");
  assert.ok(requestedInit);
  const requestedHeaders = requestedInit.headers as Record<string, string>;
  assert.equal(requestedHeaders.Accept, "application/vnd.github+json");
  assert.equal(requestedHeaders["X-GitHub-Api-Version"], "2026-03-10");
  assert.deepEqual(result, {
    status: "available",
    currentVersion: "0.2.0",
    latestVersion: "0.2.1",
    publishedAt: "2026-07-28T12:06:39Z",
    releaseUrl: "https://github.com/zxyao145/agw/releases/tag/v0.2.1",
    assetName,
    downloadUrl:
      "https://github.com/zxyao145/agw/releases/download/v0.2.1/Agw-Desktop-0.2.1-full-macos-arm64.dmg",
  });
});

test("checkForDesktopUpdate distinguishes current, prerelease, and ahead builds", async () => {
  const fetcher = async () => releaseResponse("0.2.1", []);

  assert.equal(
    (
      await checkForDesktopUpdate(fetcher, {
        ...DEFAULT_OPTIONS,
        currentVersion: "0.2.1",
      })
    ).status,
    "up-to-date",
  );
  assert.equal(
    (
      await checkForDesktopUpdate(fetcher, {
        ...DEFAULT_OPTIONS,
        currentVersion: "0.2.1-preview.2",
      })
    ).status,
    "available",
  );
  assert.equal(
    (
      await checkForDesktopUpdate(fetcher, {
        ...DEFAULT_OPTIONS,
        currentVersion: "0.3.0-preview.1",
      })
    ).status,
    "ahead",
  );
});

test("checkForDesktopUpdate keeps the release page when no compatible asset exists", async () => {
  const result = await checkForDesktopUpdate(
    async () => releaseResponse("0.2.1", ["some-other-file.zip"]),
    DEFAULT_OPTIONS,
  );

  assert.equal(result.status, "available");
  assert.equal(result.assetName, null);
  assert.equal(result.downloadUrl, null);
  assert.equal(result.releaseUrl, "https://github.com/zxyao145/agw/releases/tag/v0.2.1");
});

test("checkForDesktopUpdate rejects failed and malformed GitHub responses", async () => {
  await assert.rejects(
    () =>
      checkForDesktopUpdate(
        async () => new Response("rate limited", { status: 403 }),
        DEFAULT_OPTIONS,
      ),
    /GitHub rejected the update check \(403\)/u,
  );
  await assert.rejects(
    () => checkForDesktopUpdate(async () => Response.json({ tag_name: "v0.2.1" }), DEFAULT_OPTIONS),
    /invalid release data/u,
  );
  await assert.rejects(
    () =>
      checkForDesktopUpdate(
        async () => releaseResponse("0.2.1", [], { prerelease: true }),
        DEFAULT_OPTIONS,
      ),
    /invalid release data/u,
  );
});

test("checkForDesktopUpdate aborts a stalled request", async () => {
  const fetcher = (_input: string | Request, init?: RequestInit): Promise<Response> =>
    new Promise((_resolve, reject) => {
      init?.signal?.addEventListener("abort", () =>
        reject(new DOMException("Aborted", "AbortError")),
      );
    });

  await assert.rejects(
    () => checkForDesktopUpdate(fetcher, { ...DEFAULT_OPTIONS, timeoutMs: 1 }),
    /timed out/u,
  );
});
