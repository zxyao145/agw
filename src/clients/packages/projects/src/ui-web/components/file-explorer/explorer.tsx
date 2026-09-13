import { listFiles, type FileItem, type GitDiffScope } from "../../../services/files";
import { Select, SelectTrigger, SelectValue, SelectContent, SelectItem } from "@agw/components";
import type { ProjectDirectoryOption } from "@agw/projects-core";
import { ProjectFileScopeContext } from "./project-file-scope";
import { cn } from "@agw/components";
import ExplorerHeader from "./explorer-header";
import ExplorerFileError from "./explorer-file-error";
import ExplorerFileEmpty from "./explorer-file-empty";
import { ExplorerFileTree, ExplorerGitChangeTree } from "./explorer-file-tree";
import React from "react";
import { buildGitChangeGroups } from "./git-change-tree";
import type { GitChangeGroup } from "./types";

const ROOT_RELOAD_DEBOUNCE_MS = 150;

export default function Explorer({
  projectId,
  directoryId,
  directories = [],
  onDirectoryChange,
  rootDirectory,
  onlyDiff,
  recursiveMode,
  onOnlyDiffChange,
  onFileDeleted,
  onFileSelected,
  onFileReseted,
  onFileGitScopeChanged,
}: {
  projectId: string;
  directoryId?: string | null;
  directories?: readonly ProjectDirectoryOption[];
  onDirectoryChange?: (directoryId: string | null) => void;
  rootDirectory: string;
  onlyDiff: boolean;
  recursiveMode: boolean;
  onOnlyDiffChange?: (value: boolean) => void;

  onFileDeleted: (filePath: string) => void;
  onFileSelected: (filePath: string | null, scope?: GitDiffScope) => void;
  onFileReseted: (filePath: string | null) => void;
  onFileGitScopeChanged: (path: string, targetScope: GitDiffScope) => void;
}): React.ReactNode {
  const selectedDirectory = directories.find((directory) => directory.id === (directoryId ?? null));
  const [rootItems, setRootItems] = React.useState<FileItem[]>([]);
  const [changeGroups, setChangeGroups] = React.useState<GitChangeGroup[]>([]);
  const [isLoading, setIsLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const reloadTimeoutRef = React.useRef<ReturnType<typeof setTimeout> | null>(null);
  const rootLoadGenerationRef = React.useRef(0);
  const mountedRef = React.useRef(true);
  React.useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      rootLoadGenerationRef.current += 1;
    };
  }, []);

  const loadRootDirectory = React.useCallback(async () => {
    const generation = ++rootLoadGenerationRef.current;
    setIsLoading(true);
    setError(null);

    try {
      const data = await listFiles(projectId, "", onlyDiff, recursiveMode, undefined, directoryId);
      if (generation !== rootLoadGenerationRef.current) {
        return;
      }
      const items = data.items || [];

      if (recursiveMode && onlyDiff) {
        setChangeGroups(buildGitChangeGroups(items, ""));
        setRootItems([]);
      } else {
        setChangeGroups([]);
        setRootItems(items);
      }
    } catch (err) {
      if (generation !== rootLoadGenerationRef.current) {
        return;
      }
      console.error("Error loading root directory:", err);
      setError((err as Error).message);
      setRootItems([]);
      setChangeGroups([]);
    } finally {
      if (generation === rootLoadGenerationRef.current) {
        setIsLoading(false);
      }
    }
  }, [projectId, directoryId, rootDirectory, onlyDiff, recursiveMode]);

  React.useEffect(() => {
    loadRootDirectory();
  }, [loadRootDirectory]);

  const scheduleRootDirectoryReload = React.useCallback(() => {
    if (reloadTimeoutRef.current !== null) {
      clearTimeout(reloadTimeoutRef.current);
    }

    reloadTimeoutRef.current = setTimeout(() => {
      reloadTimeoutRef.current = null;
      void loadRootDirectory();
    }, ROOT_RELOAD_DEBOUNCE_MS);
  }, [loadRootDirectory]);

  React.useEffect(
    () => () => {
      if (reloadTimeoutRef.current !== null) {
        clearTimeout(reloadTimeoutRef.current);
        reloadTimeoutRef.current = null;
      }
    },
    [loadRootDirectory],
  );

  const handleFileDeleted = React.useCallback(
    (path: string) => {
      if (!mountedRef.current) return;
      // Reload the directory after deletion
      loadRootDirectory();
      onFileDeleted(path);
    },
    [loadRootDirectory, onFileDeleted],
  );

  const handleFileReset = React.useCallback(
    (path: string) => {
      if (!mountedRef.current) return;
      onFileReseted(path);
      loadRootDirectory();
    },
    [onFileReseted, loadRootDirectory],
  );

  const handleFileSelect = React.useCallback(
    (path: string, scope?: GitDiffScope) => {
      onFileSelected(path, scope);
    },
    [onFileSelected],
  );

  const handleGitScopeChanged = React.useCallback(
    (path: string, targetScope: GitDiffScope) => {
      if (!mountedRef.current) return;
      onFileGitScopeChanged(path, targetScope);
      scheduleRootDirectoryReload();
    },
    [onFileGitScopeChanged, scheduleRootDirectoryReload],
  );

  return (
    <ProjectFileScopeContext.Provider
      value={{
        projectId,
        directoryId,
        directoryName: selectedDirectory?.name,
      }}
    >
      <div
        className={cn(
          "border rounded-lg flex flex-col flex-1 h-full min-h-0",
          "border-0 rounded-none",
        )}
      >
        {directories.length > 1 && onDirectoryChange && (
          <div className="border-b px-2 py-2">
            <Select
              value={directoryId ?? "primary"}
              onValueChange={(value) => onDirectoryChange(value === "primary" ? null : value)}
            >
              <SelectTrigger aria-label="Project directory" className="w-full">
                <SelectValue>
                  {selectedDirectory?.name}
                  {selectedDirectory?.isPrimary ? " · Primary" : ""}
                </SelectValue>
              </SelectTrigger>
              <SelectContent>
                {directories.map((directory) => (
                  <SelectItem
                    key={directory.id ?? "primary"}
                    value={directory.id ?? "primary"}
                    textValue={`${directory.name}${directory.isPrimary ? " · Primary" : ""}`}
                  >
                    <span className="flex min-w-0 flex-col">
                      <span>
                        {directory.name}
                        {directory.isPrimary ? " · Primary" : ""}
                      </span>
                      <span className="truncate text-xs text-muted-foreground">
                        {directory.path}
                      </span>
                    </span>
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        )}
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
                onGitScopeChanged={handleGitScopeChanged}
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
    </ProjectFileScopeContext.Provider>
  );
}
