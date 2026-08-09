import { listFiles, type FileItem, type GitDiffScope } from "../../../services/files";
import { cn } from "@agw/components";
import ExplorerHeader from "./explorer-header";
import ExplorerFileError from "./explorer-file-error";
import ExplorerFileEmpty from "./explorer-file-empty";
import { ExplorerFileTree, ExplorerGitChangeTree } from "./explorer-file-tree";
import React from "react";
import { buildGitChangeGroups } from "./git-change-tree";
import type { GitChangeGroup } from "./types";

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
  onLoadFileContent: (filePath: string, scope?: GitDiffScope) => void;
  onFileSelected: (filePath: string | null, scope?: GitDiffScope) => void;
  onFileReseted: (filePath: string | null) => void;
}): React.ReactNode {
  const [rootItems, setRootItems] = React.useState<FileItem[]>([]);
  const [changeGroups, setChangeGroups] = React.useState<GitChangeGroup[]>([]);
  const [isLoading, setIsLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  const loadRootDirectory = React.useCallback(async () => {
    setIsLoading(true);
    setError(null);

    try {
      const data = await listFiles(projectId, "", onlyDiff, recursiveMode);
      const items = data.items || [];

      if (recursiveMode && onlyDiff) {
        setChangeGroups(buildGitChangeGroups(items, ""));
        setRootItems([]);
      } else {
        setChangeGroups([]);
        setRootItems(items);
      }
    } catch (err) {
      console.error("Error loading root directory:", err);
      setError((err as Error).message);
      setRootItems([]);
      setChangeGroups([]);
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
    (path: string, scope?: GitDiffScope) => {
      onFileSelected(path, scope);
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

      <div className="flex-1 min-h-0 overflow-y-auto agw-scrollbar">
        <div className="p-2">
          {error && <ExplorerFileError message={error} />}

          {!error && !isLoading && rootItems.length === 0 && changeGroups.length === 0 && (
            <ExplorerFileEmpty rootDirectory={rootDirectory} />
          )}

          {!error && changeGroups.length > 0 && (
            <ExplorerGitChangeTree
              projectId={projectId}
              groups={changeGroups}
              onFileSelect={handleFileSelect}
              onFileDeleted={handleFileDeleted}
              onFileReset={handleFileReset}
            />
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
