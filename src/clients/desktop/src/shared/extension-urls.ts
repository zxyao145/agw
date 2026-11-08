import { isServerExtensionInstanceId } from "./extension-instance";
import { isExtensionId, isValidPackagePath } from "./extension-manifest";

const SHA256_PATTERN = /^[0-9a-f]{64}$/u;

export const SERVER_EXTENSION_RELEASE_PATH_PREFIX = "/__agw/releases/";

export function serverExtensionOriginUrl(instanceId: string): string {
  if (!isServerExtensionInstanceId(instanceId)) {
    throw new Error("Invalid Server extension instance id.");
  }
  return new URL(`agw-extension://${instanceId}/`).toString();
}

export function serverExtensionReleaseUrl(
  instanceId: string,
  extensionId: string,
  releaseHash: string,
  resourcePath: string,
): string {
  if (!isServerExtensionInstanceId(instanceId)) {
    throw new Error("Invalid Server extension instance id.");
  }
  if (!isExtensionId(extensionId)) throw new Error("Invalid extension id.");
  if (!SHA256_PATTERN.test(releaseHash)) throw new Error("Invalid extension release hash.");
  if (!isValidPackagePath(resourcePath)) throw new Error("Invalid extension package path.");

  const origin = new URL(serverExtensionOriginUrl(instanceId));
  const releaseRoot = new URL(
    `${SERVER_EXTENSION_RELEASE_PATH_PREFIX.slice(1)}${releaseHash}/${extensionId}/`,
    origin,
  );
  const resourceUrl = new URL(resourcePath, releaseRoot);
  let path: string;
  try {
    path = decodeURIComponent(resourceUrl.pathname.slice(releaseRoot.pathname.length));
  } catch {
    throw new Error("Invalid extension package path.");
  }
  if (
    resourceUrl.host !== origin.host ||
    resourceUrl.protocol !== origin.protocol ||
    resourceUrl.search !== "" ||
    resourceUrl.hash !== "" ||
    !resourceUrl.pathname.startsWith(releaseRoot.pathname) ||
    !isValidPackagePath(path)
  ) {
    throw new Error("Invalid extension package path.");
  }
  return resourceUrl.toString();
}
