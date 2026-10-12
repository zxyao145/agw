import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdir, mkdtemp, rm } from "node:fs/promises";
import { join, resolve } from "node:path";
import test, { type TestContext } from "node:test";

import { parseDesktopPackageMetadata, readDevelopmentPackageMetadata } from "./package-metadata";

async function createRepository(context: TestContext): Promise<string> {
  const outputDirectory = resolve("out");
  await mkdir(outputDirectory, { recursive: true });
  const repository = await mkdtemp(join(outputDirectory, "package-metadata-"));
  context.after(() => rm(repository, { recursive: true, force: true }));
  const sourceRepository = execFileSync("git", ["rev-parse", "--show-toplevel"], {
    encoding: "utf8",
  }).trim();
  execFileSync("git", ["clone", "--bare", "--shared", "--no-tags", sourceRepository, repository], {
    stdio: "pipe",
  });
  return repository;
}

test("readDevelopmentPackageMetadata_TaggedCheckout_ReturnsTagVersion", async (context) => {
  const repository = await createRepository(context);
  execFileSync("git", ["tag", "v1.2.3"], { cwd: repository });
  execFileSync("git", ["tag", "unrelated-tag"], { cwd: repository });

  assert.deepEqual(readDevelopmentPackageMetadata(repository, "full"), {
    packageFlavor: "full",
    appVersion: "1.2.3",
  });
});

test("readDevelopmentPackageMetadata_PrereleaseCheckout_ReturnsPrereleaseVersion", async (context) => {
  const repository = await createRepository(context);
  execFileSync("git", ["tag", "v1.2.3-beta.4"], { cwd: repository });

  assert.deepEqual(readDevelopmentPackageMetadata(repository, "client"), {
    packageFlavor: "client",
    appVersion: "1.2.3-beta.4",
  });
});

test("readDevelopmentPackageMetadata_UntaggedCheckout_Throws", async (context) => {
  const repository = await createRepository(context);

  assert.throws(() => readDevelopmentPackageMetadata(repository, "full"));
});

test("readDevelopmentPackageMetadata_UnsupportedTag_Throws", async (context) => {
  const repository = await createRepository(context);
  execFileSync("git", ["tag", "v1.2.3-rc.1"], { cwd: repository });

  assert.throws(() => readDevelopmentPackageMetadata(repository, "full"), /package metadata/u);
});

test("parseDesktopPackageMetadata reads the packaged flavor and injected release version", () => {
  assert.deepEqual(
    parseDesktopPackageMetadata({ packageFlavor: "client", appVersion: "1.2.3-beta.4" }),
    { packageFlavor: "client", appVersion: "1.2.3-beta.4" },
  );
  assert.deepEqual(parseDesktopPackageMetadata({ packageFlavor: "full", appVersion: "1.2.3" }), {
    packageFlavor: "full",
    appVersion: "1.2.3",
  });
});

test("parseDesktopPackageMetadata rejects incomplete or invalid packaged metadata", () => {
  for (const value of [
    null,
    {},
    { packageFlavor: "enterprise", appVersion: "1.2.3" },
    { packageFlavor: "full", appVersion: "v1.2.3" },
  ]) {
    assert.throws(() => parseDesktopPackageMetadata(value), /package metadata/u);
  }
});
