import type { ExecutionMessage, PermissionMode, PermissionStatus } from "@agw/execution-types";

export const PERMISSION_LABELS: Record<PermissionMode, string> = {
  fullAccess: "Full access",
  alwaysAsk: "Always ask",
  allowSameArguments: "Allow same arguments",
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
