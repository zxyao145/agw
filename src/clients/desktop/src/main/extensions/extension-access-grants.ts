import { isPathInside } from "./extension-catalog";
import { judgementPath } from "./extension-path-judgement";

type LocalGrant = {
  path: string;
  access: "read" | "readWrite";
  /** `tree` 包含其下全部内容；`file` 只包含这一项。`tree` covers everything below; `file` covers this entry only. */
  scope: "tree" | "file";
};

/** 本次激活内的本地用户选择授权。Local user-selection grants for the current activation. */
export class ExtensionAccessGrants {
  private readonly grants = new Map<string, LocalGrant[]>();

  /** `showOpenDialog` 返回的路径可读，目录包含其下全部内容。Open-dialog paths are readable, directories recursively. */
  public async grantRead(extensionId: string, path: string): Promise<void> {
    this.add(extensionId, { path: await judgementPath(path), access: "read", scope: "tree" });
  }

  /** `showSaveDialog` 返回的路径可读写，只覆盖这一个文件。Save-dialog paths are readable and writable, for that file only. */
  public async grantWrite(extensionId: string, path: string): Promise<void> {
    this.add(extensionId, { path: await judgementPath(path), access: "readWrite", scope: "file" });
  }

  public allows(extensionId: string, judgedPath: string, access: "read" | "write"): boolean {
    return (this.grants.get(extensionId) ?? []).some(
      (grant) =>
        (access === "read" || grant.access === "readWrite") &&
        (grant.scope === "tree" ? isPathInside(grant.path, judgedPath) : grant.path === judgedPath),
    );
  }

  public release(extensionId: string): void {
    this.grants.delete(extensionId);
  }

  public releaseAll(): void {
    this.grants.clear();
  }

  private add(extensionId: string, grant: LocalGrant): void {
    const grants = this.grants.get(extensionId) ?? [];
    grants.push(grant);
    this.grants.set(extensionId, grants);
  }
}
