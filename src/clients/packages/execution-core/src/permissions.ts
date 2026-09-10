import type { ExecutionMessage } from "./types";
import type { PermissionMode } from "./protocol";

export const PERMISSION_LABELS: Record<PermissionMode, string> = {
  fullAccess: "Full access",
  alwaysAsk: "Always ask",
  allowSameArguments: "Allow same arguments",
};

export type PermissionStatus = {
  activePermissionMode: PermissionMode | null;
  nextPermissionMode: PermissionMode | null;
  permissionChangePending: boolean;
};

function readMode(value: unknown): PermissionMode | null {
  return value === "fullAccess" || value === "alwaysAsk" || value === "allowSameArguments"
    ? value
    : null;
}

export function getPermissionStatus(message: ExecutionMessage): PermissionStatus | null {
  const properties = message.additionalProperties;
  if (properties?.type !== "permission-status") return null;
  return {
    activePermissionMode: readMode(properties.activePermissionMode),
    nextPermissionMode: readMode(properties.nextPermissionMode),
    permissionChangePending: properties.permissionChangePending === true,
  };
}
