import { listFiles, type FileItem } from "../../../services/files";
import { cn } from "@agw/components";
import ExplorerHeader from "./explorer-header";
import ExplorerFileError from "./explorer-file-error";
import ExplorerFileEmpty from "./explorer-file-empty";
import { ExplorerFileTree } from "./explorer-file-tree";
import React from "react";
import { FileItemType, GitStatus, type GitStatus as GitStatusValue } from "./types";

/**
 * Build a tree structure from a flat list of file paths
 * Used in recursive mode to display changed files in a directory tree
 */
function buildFileTree(items: FileItem[], rootPath: string): FileItem[] {
  // Normalize the root path
  const normalizedRoot = rootPath.replace(/\\/g, "/").replace(/\/$/, "");

  // Create a map to store directory items
  const dirMap = new Map<string, FileItem & { children: FileItem[] }>();

  // Sort items by path depth (shallow first) to ensure parents are created before children
  const sortedItems = [...items].sort((a, b) => {
    const aDepth = a.path.replace(/\\/g, "/").split("/").length;
    const bDepth = b.path.replace(/\\/g, "/").split("/").length;
    return aDepth - bDepth;
  });

  // Collect all git statuses for directory aggregation
  const fileStatusesMap = new Map<string, Set<string>>();

  sortedItems.forEach((item) => {
    const normalizedPath = item.path.replace(/\\/g, "/");

    // Track statuses for each directory
    let currentPath = normalizedPath;
    while (currentPath !== normalizedRoot && currentPath !== "") {
      if (!fileStatusesMap.has(currentPath)) {
        fileStatusesMap.set(currentPath, new Set());
      }
      if (item.gitStatus) {
        fileStatusesMap.get(currentPath)!.add(item.gitStatus);
      }

      currentPath = currentPath.substring(0, currentPath.lastIndexOf("/"));
      if (currentPath === "") break;
    }
  });

  // Helper to get aggregated git status for a directory
  const getDirGitStatus = (path: string): GitStatusValue | undefined => {
    const statuses = fileStatusesMap.get(path);
    if (!statuses || statuses.size === 0) return undefined;

    if (statuses.has(GitStatus.Modified)) return GitStatus.Modified;
    if (statuses.has(GitStatus.Added)) return GitStatus.Added;
    if (statuses.has(GitStatus.Untracked)) return GitStatus.Untracked;
    if (statuses.has(GitStatus.Deleted)) return GitStatus.Deleted;

    return undefined;
  };

  // Build tree structure
  sortedItems.forEach((item) => {
    const normalizedPath = item.path.replace(/\\/g, "/");

    // Get parent path
    const parentPath = normalizedPath.substring(0, normalizedPath.lastIndexOf("/"));

    // Ensure all parent directories exist
    let currentPath = parentPath;
    const pathsToCreate: string[] = [];

    while (currentPath !== normalizedRoot && currentPath !== "") {
      if (!dirMap.has(currentPath)) {
        pathsToCreate.unshift(currentPath);
      }
      currentPath = currentPath.substring(0, currentPath.lastIndexOf("/"));
      if (currentPath === "") break;
    }

    // Create missing parent directories
    pathsToCreate.forEach((path) => {
      const dirName = path.substring(path.lastIndexOf("/") + 1);
      dirMap.set(path, {
        name: dirName,
        path: path,
        type: FileItemType.Directory,
        gitStatus: getDirGitStatus(path),
        children: [],
      });
    });

    // Add file to its parent directory
    if (item.type === FileItemType.File) {
      if (parentPath === normalizedRoot || parentPath === "") {
        // File at root level - will be added later
      } else if (dirMap.has(parentPath)) {
        dirMap.get(parentPath)!.children.push(item);
      }
    } else if (item.type === FileItemType.Directory) {
      // Create or update directory
      if (!dirMap.has(normalizedPath)) {
        dirMap.set(normalizedPath, {
          ...item,
          children: [],
        });
      }
    }
  });

  // Build the tree recursively
  const buildTree = (dirPath: string | null, items: FileItem[]): FileItem[] => {
    const result: FileItem[] = [];

    // Get all items at this level
    const levelMap = new Map<string, FileItem & { children?: FileItem[] }>();

    // Process all directories
    dirMap.forEach((dir, path) => {
      const parentPath = path.substring(0, path.lastIndexOf("/")) || normalizedRoot;
      const normalizedParent = parentPath.replace(/\\/g, "/");

      if (
        (dirPath === null && normalizedParent === normalizedRoot) ||
        (dirPath !== null && normalizedParent === dirPath)
      ) {
        levelMap.set(path, { ...dir });
      }
    });

    // Process files at this level
    items.forEach((item) => {
      const normalizedPath = item.path.replace(/\\/g, "/");
      const parentPath =
        normalizedPath.substring(0, normalizedPath.lastIndexOf("/")) || normalizedRoot;
      const normalizedParent = parentPath.replace(/\\/g, "/");

      if (
        (dirPath === null && normalizedParent === normalizedRoot) ||
        (dirPath !== null && normalizedParent === dirPath)
      ) {
        const key = `file-${normalizedPath}`;
        levelMap.set(key, { ...item });
      }
    });

    // Build and sort items at this level
    levelMap.forEach((item) => {
      if (item.type === FileItemType.Directory) {
        const dirWithChildren = dirMap.get(item.path);
        if (dirWithChildren) {
          item.children = buildTree(item.path, items);
        }
      }
      result.push(item);
    });

    // Sort: directories first, then by name
    return result.sort((a, b) => {
      if (a.type === b.type) {
        return a.name.localeCompare(b.name, undefined, {
          numeric: true,
          sensitivity: "base",
        });
      }
      return a.type === FileItemType.Directory ? -1 : 1;
    });
  };

  return buildTree(null, items);
}

export default function Explorer({
  projectId,
  rootDirectory,
  onlyDiff,
  recursiveMode,
  onOnlyDiffChange,
  onFileDeleted,
  onLoadFileContent,
  onFileSelected,
  onFileReseted,
}: {
  projectId: string;
  rootDirectory: string;
  onlyDiff: boolean;
  recursiveMode: boolean;
  onOnlyDiffChange?: (value: boolean) => void;

  onFileDeleted: (filePath: string) => void;
  onLoadFileContent: (filePath: string) => void;
  onFileSelected: (filePath: string | null) => void;
  onFileReseted: (filePath: string | null) => void;
}): React.ReactNode {
  const [rootItems, setRootItems] = React.useState<FileItem[]>([]);
  const [isLoading, setIsLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  const loadRootDirectory = React.useCallback(async () => {
    setIsLoading(true);
    setError(null);

    try {
      const data = await listFiles(projectId, "", onlyDiff, recursiveMode);
      let items = data.items || [];

      // In recursive mode with diff, build a tree structure
      if (recursiveMode && onlyDiff) {
        items = buildFileTree(items, "");
      }

      setRootItems(items);
    } catch (err) {
      console.error("Error loading root directory:", err);
      setError((err as Error).message);
      setRootItems([]);
    } finally {
      setIsLoading(false);
    }
  }, [projectId, onlyDiff, recursiveMode]);

  React.useEffect(() => {
    loadRootDirectory();
  }, [loadRootDirectory]);

  const handleFileDeleted = React.useCallback(
    (path: string) => {
      // Reload the directory after deletion
      loadRootDirectory();
      onFileDeleted(path);
    },
    [loadRootDirectory],
  );

  const handleFileReset = React.useCallback(
    (path: string) => {
      onFileReseted(path);
      loadRootDirectory();
    },
    [onLoadFileContent, loadRootDirectory],
  );

  const handleFileSelect = React.useCallback(
    (path: string) => {
      onFileSelected(path);
    },
    [onFileSelected],
  );

  return (
    <div
      className={cn(
        "border rounded-lg flex flex-col flex-1 h-full min-h-0",
        "border-0 rounded-none",
      )}
    >
      <ExplorerHeader
        isLoading={isLoading}
        loadRootDirectory={loadRootDirectory}
        rootDirectory={rootDirectory}
        onlyDiff={onlyDiff}
        onOnlyDiffChange={onOnlyDiffChange}
      />

      <div className="flex-1 min-h-0 overflow-y-auto">
        <div className="p-2">
          {error && <ExplorerFileError message={error} />}

          {!error && !isLoading && rootItems.length === 0 && (
            <ExplorerFileEmpty rootDirectory={rootDirectory} />
          )}

          {!error && rootItems.length > 0 && (
            <ExplorerFileTree
              projectId={projectId}
              rootItems={rootItems}
              onlyDiff={onlyDiff}
              recursiveMode={recursiveMode}
              onFileSelect={handleFileSelect}
              onFileDeleted={handleFileDeleted}
              onFileReset={handleFileReset}
            />
          )}
        </div>
      </div>
    </div>
  );
}
