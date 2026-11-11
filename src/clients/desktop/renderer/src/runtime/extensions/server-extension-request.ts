import { createExtensionError } from "@agw/extension-sdk";

const TECHNICAL_NAME_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/u;

function invalidArgument(message: string): Error {
  return createExtensionError("ERR_INVALID_ARGUMENT", message);
}

export function isExtensionVersionMismatch(error: unknown): boolean {
  if (typeof error !== "object" || error === null || !("body" in error)) return false;
  const body = error.body;
  return typeof body === "object" && body !== null && "code" in body && body.code === 4090027;
}

export function extensionServerPath(
  extensionName: string,
  path: string,
  method: string = "GET",
): string {
  if (!TECHNICAL_NAME_PATTERN.test(extensionName)) {
    throw invalidArgument("The extension technical name is invalid.");
  }
  if (path === "/api/notifications/answer") {
    if (method.toUpperCase() !== "POST") {
      throw invalidArgument("The Notifications answer endpoint only supports POST requests.");
    }
    return path;
  }
  if (
    typeof path !== "string" ||
    path.startsWith("//") ||
    /^[a-z][a-z0-9+.-]*:/iu.test(path) ||
    path.includes("?") ||
    path.includes("#") ||
    path.includes("\\")
  ) {
    throw invalidArgument("The Server request path must be relative to the extension endpoint.");
  }

  const relativePath = path.startsWith("/") ? path.slice(1) : path;
  if (!relativePath) return "/api/ext/" + extensionName + "/";
  const hasTrailingSlash = relativePath.endsWith("/");
  const segments = relativePath.split("/");
  if (hasTrailingSlash) segments.pop();
  if (segments.some((segment) => segment.length === 0)) {
    throw invalidArgument("The Server request path contains an empty segment.");
  }

  const encoded = segments.map((segment) => {
    let decoded: string;
    try {
      decoded = decodeURIComponent(segment);
    } catch {
      throw invalidArgument("The Server request path has invalid percent encoding.");
    }
    if (decoded === "." || decoded === ".." || decoded.includes("/") || decoded.includes("\\")) {
      throw invalidArgument("The Server request path escapes the extension endpoint.");
    }
    return encodeURIComponent(decoded);
  });
  return "/api/ext/" + extensionName + "/" + encoded.join("/") + (hasTrailingSlash ? "/" : "");
}
