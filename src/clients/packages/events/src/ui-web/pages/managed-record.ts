const MANAGED_BY_USER = "user";
const MANAGED_BY_JOB = "job";
const MANAGED_BY_EXTENSION_PREFIX = "ext:";

/**
 * 返回受管理的来源或订阅的标记文字；用户创建的记录返回 null，可以在 Events 页面修改。
 * Returns the badge text of a managed source or subscription; a record created by the user returns null and is editable on the Events page.
 */
export function getManagedLabel(managedBy: string, managedKey?: string | null): string | null {
  if (managedBy === MANAGED_BY_USER) {
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
