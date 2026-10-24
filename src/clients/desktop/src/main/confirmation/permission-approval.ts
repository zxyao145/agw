import semver from "semver";

import { type ExtensionManifest, HIGH_RISK_EXTENSION_PERMISSIONS } from "@agw/extension-sdk";
import type { PermissionApprovalView } from "../../shared/contracts";
import {
  isUnencryptedHostPermission,
  parseHostPermission,
} from "../../shared/extension-host-permissions";
import {
  PERMISSION_DESCRIPTIONS,
  READ_PERMISSIONS,
} from "../../shared/extension-permission-descriptions";

/**
 * 由已校验的 manifest 生成权限批准内容；权限说明与提示全部来自应用。
 * Builds the permission approval content from a validated manifest; descriptions and notes all come from the app.
 */
export function permissionApprovalView(
  kind: "install" | "enable",
  manifest: ExtensionManifest,
  installedVersion: string | null,
): PermissionApprovalView {
  const permissions = manifest.permissions ?? [];
  const hosts = manifest.hostPermissions ?? [];
  return {
    kind,
    extensionId: manifest.id,
    extensionName: manifest.name,
    version: manifest.version,
    installedVersion,
    downgrade: installedVersion !== null && semver.lt(manifest.version, installedVersion),
    permissions: permissions.map((name) => ({
      name,
      description: PERMISSION_DESCRIPTIONS[name],
      highRisk: HIGH_RISK_EXTENSION_PERMISSIONS.has(name),
    })),
    hostPermissions: hosts.map((pattern) => {
      const parsed = parseHostPermission(pattern);
      return { pattern, unencrypted: parsed !== null && isUnencryptedHostPermission(parsed) };
    }),
    sendsData: hosts.length > 0 && permissions.some((name) => READ_PERMISSIONS.includes(name)),
    runsAgents: permissions.includes("execution"),
  };
}
