import type { PackageFlavor } from "../shared/contracts";
import { isSupportedDesktopVersion } from "./update/github-release-updater";

export type DesktopPackageMetadata = {
  packageFlavor: PackageFlavor;
  appVersion: string;
};

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
