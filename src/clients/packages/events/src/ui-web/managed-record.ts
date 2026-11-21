export const MANAGED_BY_USER = "user";
export const MANAGED_BY_JOB = "job";
export const MANAGED_BY_AGW = "agw";
const MANAGED_BY_EXTENSION_PREFIX = "ext:";

export function isUserManaged(managedBy: string): boolean {
  return managedBy === MANAGED_BY_USER;
}

/**
 * 返回受管理的来源或订阅的标记文字。用户创建的记录返回 null。
 * Returns the badge text for a managed source or subscription. User-created records return null.
 */
export function getManagedLabel(managedBy: string, managedKey?: string | null): string | null {
  if (isUserManaged(managedBy)) {
    return null;
  }
  if (managedBy === MANAGED_BY_JOB) {
    return `Managed by Job ${managedKey}`;
  }
  if (managedBy.startsWith(MANAGED_BY_EXTENSION_PREFIX)) {
    return `Managed by extension ${managedBy.slice(MANAGED_BY_EXTENSION_PREFIX.length)}`;
  }
  return `Managed by ${managedBy}`;
}
