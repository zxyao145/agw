import assert from "node:assert/strict";
import test from "node:test";

import { parseDesktopPackageMetadata } from "./package-metadata";

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
