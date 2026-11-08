import { createHash } from "node:crypto";

import { isExtensionId } from "../../shared/extension-manifest";

export { isExtensionInstanceId } from "../../shared/extension-instance";

export type ServerExtensionOrigin = {
  serverId: string;
  userId: string;
};

export function extensionInstanceId(
  origin: ServerExtensionOrigin | null,
  extensionId: string,
): string {
  if (!isExtensionId(extensionId)) throw new Error("Invalid extension id.");
  if (origin === null) return extensionId;
  if (!origin.serverId.trim() || !origin.userId.trim()) {
    throw new Error("A Server extension instance requires a Server and user identity.");
  }
  const identity = JSON.stringify([origin.serverId, origin.userId, extensionId]);
  const digest = createHash("sha256").update(identity).digest("hex");
  return `server-${digest}`;
}
