/** extension 可以声明的权限。Permissions an extension can declare. */
export const EXTENSION_PERMISSIONS = [
  "context.user",
  "context.project",
  "context.agent",
  "fs.local.read",
  "fs.local.write",
  "fs.cloud.read",
  "fs.cloud.write",
  "execution",
  "a2a",
  "chat.composer",
  "chat.observe",
  "clipboard.read",
  "clipboard.write",
] as const;

export type ExtensionPermission = (typeof EXTENSION_PERMISSIONS)[number];

/** 批准弹窗中用警告色显示的权限。Permissions shown with a warning color in the approval dialog. */
export const HIGH_RISK_EXTENSION_PERMISSIONS: ReadonlySet<ExtensionPermission> = new Set([
  "fs.local.read",
  "fs.local.write",
  "fs.cloud.write",
  "execution",
  "a2a",
  "chat.observe",
  "clipboard.read",
]);

export function isExtensionPermission(value: string): value is ExtensionPermission {
  return (EXTENSION_PERMISSIONS as readonly string[]).includes(value);
}
