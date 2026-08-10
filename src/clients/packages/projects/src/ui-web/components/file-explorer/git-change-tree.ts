import type { FileItem, FileGitStatus, GitDiffScope } from "../../../services/files";
import { FileItemType, type FileTreeItem, type GitChangeGroup } from "./types";

const groupDefinitions: Array<{ scope: GitDiffScope; label: string }> = [
  { scope: "staged", label: "Staged" },
  { scope: "unstaged", label: "Unstaged" },
];

export function buildGitChangeGroups(items: FileItem[], rootPath: string): GitChangeGroup[] {
  return groupDefinitions
    .map(({ scope, label }) => {
      const scopedItems = items.filter((item) => Boolean(getScopedStatus(item, scope)));
      return {
        scope,
        label,
        fileCount: scopedItems.length,
        items: buildScopedFileTree(scopedItems, rootPath, scope),
      };
    })
    .filter((group) => group.fileCount > 0);
}

export function formatFileCount(count: number): string {
  return `${count} ${count === 1 ? "file" : "files"}`;
}

function buildScopedFileTree(
  items: FileItem[],
  rootPath: string,
  scope: GitDiffScope,
): FileTreeItem[] {
  const normalizedRoot = normalizePath(rootPath);
  const rootItems: FileTreeItem[] = [];
  const directories = new Map<string, FileTreeItem>();

  items.forEach((item) => {
    if (item.type !== FileItemType.File) return;

    const scopedStatus = getScopedStatus(item, scope);
    if (!scopedStatus) return;

    const normalizedPath = normalizePath(item.path);
    const relativePath = getRelativePath(normalizedPath, normalizedRoot);
    const segments = relativePath.split("/").filter(Boolean);
    if (segments.length === 0) return;

    let currentPath = normalizedRoot;
    let siblings = rootItems;
    for (const segment of segments.slice(0, -1)) {
      currentPath = currentPath ? `${currentPath}/${segment}` : segment;
      let directory = directories.get(currentPath);
      if (!directory) {
        directory = {
          name: segment,
          path: currentPath,
          type: FileItemType.Directory,
          children: [],
          changeCount: 0,
          gitScope: scope,
        };
        directories.set(currentPath, directory);
        siblings.push(directory);
      }

      directory.changeCount = (directory.changeCount ?? 0) + 1;
      siblings = directory.children ?? [];
    }

    siblings.push({
      ...item,
      name: segments.at(-1) ?? item.name,
      path: normalizedPath,
      gitStatus: scopedStatus,
      gitScope: scope,
      changeCount: 1,
      children: undefined,
    });
  });

  return sortTree(rootItems).map(compactDirectoryChain);
}

function sortTree(items: FileTreeItem[]): FileTreeItem[] {
  items.forEach((item) => {
    if (item.children) {
      item.children = sortTree(item.children);
    }
  });

  return items.sort((a, b) => {
    if (a.type !== b.type) {
      return a.type === FileItemType.Directory ? -1 : 1;
    }

    return a.name.localeCompare(b.name, undefined, {
      numeric: true,
      sensitivity: "base",
    });
  });
}

function compactDirectoryChain(item: FileTreeItem): FileTreeItem {
  if (item.type !== FileItemType.Directory) return item;

  let compacted: FileTreeItem = {
    ...item,
    children: item.children?.map(compactDirectoryChain),
  };
  while (
    compacted.children?.length === 1 &&
    compacted.children[0].type === FileItemType.Directory
  ) {
    const child = compacted.children[0];
    compacted = {
      ...compacted,
      name: `${compacted.name}/${child.name}`,
      path: child.path,
      children: child.children,
      changeCount: child.changeCount,
    };
  }

  return compacted;
}

function getScopedStatus(item: FileItem, scope: GitDiffScope): FileGitStatus | null | undefined {
  const hasScopedStatuses = "gitStagedStatus" in item || "gitUnstagedStatus" in item;
  if (!hasScopedStatuses) {
    return scope === "unstaged" ? item.gitStatus : undefined;
  }

  return scope === "staged" ? item.gitStagedStatus : item.gitUnstagedStatus;
}

function getRelativePath(path: string, rootPath: string): string {
  if (!rootPath) return path;
  if (path === rootPath) return "";
  const prefix = `${rootPath}/`;
  return path.startsWith(prefix) ? path.slice(prefix.length) : path;
}

function normalizePath(path: string): string {
  return path.replace(/\\/g, "/").replace(/^\/+|\/+$/g, "");
}
