import { execFileSync } from "node:child_process";

import type { PackageFlavor } from "../shared/contracts";
import { isSupportedDesktopVersion } from "./update/github-release-updater";

export type DesktopPackageMetadata = {
  packageFlavor: PackageFlavor;
  appVersion: string;
};

export function readDevelopmentPackageMetadata(
  applicationPath: string,
  packageFlavor: PackageFlavor,
): DesktopPackageMetadata {
  const tag = execFileSync("git", ["describe", "--tags", "--match", "v[0-9]*", "--abbrev=0"], {
    cwd: applicationPath,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  }).trim();

  return parseDesktopPackageMetadata({ packageFlavor, appVersion: tag.slice(1) });
}

export function parseDesktopPackageMetadata(value: unknown): DesktopPackageMetadata {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error("Invalid Desktop package metadata.");
  }

  const metadata = value as Record<string, unknown>;
  if (
    (metadata.packageFlavor !== "full" && metadata.packageFlavor !== "client") ||
    typeof metadata.appVersion !== "string" ||
    !isSupportedDesktopVersion(metadata.appVersion)
  ) {
    throw new Error("Invalid Desktop package metadata.");
  }

  return {
    packageFlavor: metadata.packageFlavor,
    appVersion: metadata.appVersion,
  };
}
