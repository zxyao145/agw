"use client";

import * as React from "react";
import { useProjectFileScope } from "./project-file-scope";
import {
  Folder,
  FolderOpen,
  File,
  FileText,
  FileCode,
  FileJson,
  Image,
  ChevronRight,
  ChevronDown,
  Loader2,
  Minus,
  Plus,
  Trash2,
  RotateCcw,
} from "lucide-react";
import { cn } from "@agw/components";
import {
  deleteFile,
  listFiles,
  resetFile,
  setFileStaged,
  type FileItem,
  type GitDiffScope,
} from "../../../services/files";
import type { ExplorerContextMenuArgs, ExtensionMenuItem } from "../../../lib/extension-menu";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@agw/components";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@agw/components";
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from "@agw/components";
import { toast } from "sonner";
import { formatFileCount } from "./git-change-tree";
import {
  FileItemType,
  FileTreeNodeProps,
  GitStatus,
  GitStatusBadgeLabel,
  type FileTreeItem,
  type FileTreeSelectionHandler,
  type GitChangeGroup,
} from "./types";

const getFileIcon = (fileName: string) => {
  const ext = fileName.split(".").pop()?.toLowerCase();

  switch (ext) {
    case "js":
    case "ts":
    case "jsx":
    case "tsx":
    case "py":
    case "java":
    case "cpp":
    case "c":
    case "cs":
    case "go":
    case "rs":
      return FileCode;
    case "json":
      return FileJson;
    case "txt":
    case "md":
    case "mdx":
      return FileText;
    case "png":
    case "jpg":
    case "jpeg":
    case "gif":
    case "svg":
    case "webp":
      return Image;
    default:
      return File;
  }
};

const formatFileSize = (bytes: number): string => {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
};

function useFileTreeSelection(
  selectedPath: string | null | undefined,
  onFileSelect: FileTreeSelectionHandler,
) {
  const [revealPath, setRevealPath] = React.useState(selectedPath);
  const [clickedPath, setClickedPath] = React.useState<string | null>(null);
  const previousPathRef = React.useRef(selectedPath);

  React.useEffect(() => {
    if (previousPathRef.current !== selectedPath) {
      previousPathRef.current = selectedPath;
      setRevealPath(selectedPath === clickedPath ? null : selectedPath);
    }
    if (clickedPath !== null) setClickedPath(null);
  }, [selectedPath, clickedPath]);

  const handleFileSelect: FileTreeSelectionHandler = (path, scope, options) => {
    if (selectedPath !== undefined) setClickedPath(path);
    onFileSelect(path, scope, options);
  };

  return {
    revealPath: revealPath === selectedPath ? revealPath : null,
    onFileSelect: handleFileSelect,
    onFileRevealed: () => setRevealPath(null),
  };
}

function FileTreeNode({
  projectId,
  item,
  onFileSelect,
  selectedPath,
  revealPath,
  onFileRevealed,
  level,
  diffMode,
  recursiveMode,
  onFileDeleted,
  onFileReset,
  onGitScopeChanged,
  defaultExpanded,
  contextMenuItems,
  refreshSignal,
}: FileTreeNodeProps) {
  const { directoryId } = useProjectFileScope();
  const [isExpanded, setIsExpanded] = React.useState(
    defaultExpanded && item.type === FileItemType.Directory && (item.children?.length ?? 0) > 0,
  );
  // Use item.children if available (pre-built tree), otherwise empty for lazy loading
  const initialChildren = item.children || [];
  const [children, setChildren] = React.useState<FileTreeItem[]>(initialChildren);
  const [isLoading, setIsLoading] = React.useState(false);
  const [isDeleteDialogOpen, setIsDeleteDialogOpen] = React.useState(false);
  const [isDeleting, setIsDeleting] = React.useState(false);
  const [isUpdatingGitScope, setIsUpdatingGitScope] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const rowRef = React.useRef<HTMLDivElement>(null);
  const childrenLoadGenerationRef = React.useRef(0);
  const childrenLoadedRef = React.useRef(false);
  // Update children when item.children change (for pre-built trees)
  React.useEffect(() => {
    if (item.children) {
      setChildren(item.children);
    }
  }, [item.children]);

  const loadChildren = async (reload = false) => {
    if (item.type !== FileItemType.Directory || (!reload && children.length > 0)) return;

    const generation = ++childrenLoadGenerationRef.current;
    setIsLoading(true);
    setError(null);

    try {
      const data = await listFiles(
        projectId,
        item.path,
        diffMode,
        recursiveMode,
        undefined,
        directoryId,
      );
      if (generation !== childrenLoadGenerationRef.current) {
        return;
      }
      setChildren(data.items || []);
      childrenLoadedRef.current = true;
    } catch (err) {
      if (generation !== childrenLoadGenerationRef.current) {
        return;
      }
      console.error("Error loading directory:", err);
      setError((err as Error).message);
    } finally {
      if (generation === childrenLoadGenerationRef.current) {
        setIsLoading(false);
      }
    }
  };

  const reveal = React.useEffectEvent(() => {
    if (item.type === FileItemType.Directory && revealPath?.startsWith(`${item.path}/`)) {
      setIsExpanded(true);
      if (!item.children && !isLoading && !childrenLoadedRef.current) void loadChildren();
    } else if (item.type === FileItemType.File && item.path === revealPath) {
      rowRef.current?.scrollIntoView?.({ block: "nearest" });
      onFileRevealed();
    }
  });
  React.useEffect(() => {
    if (revealPath) reveal();
  }, [revealPath, item.path, item.type]);

  // 跳过第一次的值；已展开的目录重新读取子项，未展开的目录丢弃旧子项，下次展开时读取。
  // Skip the first value; an expanded directory rereads its children, a collapsed one drops them and reads on the next expand.
  const observedRefreshSignalRef = React.useRef(refreshSignal);
  React.useEffect(() => {
    if (observedRefreshSignalRef.current === refreshSignal) return;
    observedRefreshSignalRef.current = refreshSignal;
    if (item.type !== FileItemType.Directory || item.children) return;
    if (isExpanded) {
      void loadChildren(true);
    } else {
      childrenLoadGenerationRef.current += 1;
      childrenLoadedRef.current = false;
      setIsLoading(false);
      setChildren([]);
    }
  }, [refreshSignal]);

  const handleToggle = () => {
    if (item.type === FileItemType.Directory) {
      setIsExpanded(!isExpanded);
      if (!isExpanded && children.length === 0 && !item.children) {
        loadChildren();
      }
    }
  };

  const handleClick = () => {
    if (item.type === FileItemType.File) {
      onFileSelect(item.path, item.gitScope, { pinned: false });
    } else {
      handleToggle();
    }
  };

  const handleDeleteRequest = (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setIsDeleteDialogOpen(true);
  };

  const handleDelete = async (e: React.MouseEvent) => {
    e.preventDefault();
    setIsDeleting(true);

    try {
      const result = await deleteFile(projectId, item.path, undefined, directoryId);
      if (result.success) {
        setIsDeleteDialogOpen(false);
        toast.success(result.message);
        onFileDeleted?.(item.path);
      } else {
        toast.error(result.message || "Failed to delete");
      }
    } catch (err) {
      console.error("Error deleting:", err);
      toast.error((err as Error).message);
    } finally {
      setIsDeleting(false);
    }
  };

  const handleReset = async (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();

    if (item.type !== FileItemType.File) {
      toast.error("Can only reset files, not directories");
      return;
    }

    try {
      const result = await resetFile(projectId, item.path, undefined, directoryId);
      if (result.success) {
        toast.success(result.message);
        onFileReset?.(item.path);
      } else {
        toast.info(result.message || "No changes to reset");
      }
    } catch (err) {
      console.error("Error resetting:", err);
      toast.error((err as Error).message);
    }
  };

  const handleGitScopeChange = async (event: React.MouseEvent<HTMLButtonElement>) => {
    event.preventDefault();
    event.stopPropagation();
    if (!item.gitScope || isUpdatingGitScope) return;

    const targetScope: GitDiffScope = item.gitScope === "staged" ? "unstaged" : "staged";
    setIsUpdatingGitScope(true);
    try {
      const result = await setFileStaged(
        projectId,
        item.path,
        targetScope === "staged",
        undefined,
        directoryId,
      );
      if (result.success) {
        onGitScopeChanged?.(item.path, targetScope);
      } else {
        toast.error(result.message || `Failed to move changes to ${targetScope}`);
      }
    } catch (err) {
      console.error(`Error moving changes to ${targetScope}:`, err);
      toast.error((err as Error).message);
    } finally {
      setIsUpdatingGitScope(false);
    }
  };

  const FileIcon = item.type === FileItemType.File ? getFileIcon(item.name) : null;
  const FolderIcon = isExpanded ? FolderOpen : Folder;
  const ChevronIcon = isExpanded ? ChevronDown : ChevronRight;

  const hasChildren = item.type === FileItemType.Directory && children.length > 0;
  const isSelectable = selectedPath !== undefined;
  const isFile = item.type === FileItemType.File;
  const isSelected = isFile && item.path === selectedPath;

  return (
    <div
      ref={rowRef}
      role={isSelectable ? "treeitem" : undefined}
      aria-label={isSelectable ? item.name : undefined}
      aria-selected={isSelectable && isFile ? isSelected : undefined}
      aria-expanded={isSelectable && !isFile ? isExpanded : undefined}
    >
      {/* Avoid stacking Radix pointer locks with the delete dialog when this node unmounts. */}
      <ContextMenu modal={false}>
        <ContextMenuTrigger asChild>
          <div
            className={cn(
              "flex items-center gap-1 py-1 px-2 hover:bg-accent hover:text-accent-foreground cursor-pointer rounded-sm group",
              "transition-colors",
              isSelected && "bg-accent text-accent-foreground",
            )}
            style={{ paddingLeft: `${level * 12 + 8}px` }}
            onClick={handleClick}
            onDoubleClick={
              isFile ? () => onFileSelect(item.path, item.gitScope, { pinned: true }) : undefined
            }
          >
            {item.type === FileItemType.Directory && (
              <ChevronIcon className="h-4 w-4 shrink-0 text-muted-foreground" />
            )}
            {item.type === FileItemType.File && <div className="w-4" />}

            {item.type === FileItemType.Directory ? (
              <FolderIcon className="h-4 w-4 shrink-0 text-blue-500" />
            ) : (
              FileIcon && <FileIcon className="h-4 w-4 shrink-0 text-muted-foreground" />
            )}

            <span
              className={cn(
                "text-sm truncate flex-1",
                item.gitStatus === GitStatus.Deleted && "line-through text-muted-foreground",
              )}
            >
              {item.name}
            </span>

            {item.type === FileItemType.Directory && item.changeCount !== undefined && (
              <span className="shrink-0 text-xs text-muted-foreground">
                {formatFileCount(item.changeCount)}
              </span>
            )}

            {/* Git status indicator */}
            {item.gitStatus && (
              <span
                className={cn(
                  "text-2xs font-medium px-1.5 py-0.5 rounded",
                  item.gitStatus === GitStatus.Added &&
                    "bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400",
                  item.gitStatus === GitStatus.Modified &&
                    "bg-yellow-100 text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400",
                  item.gitStatus === GitStatus.Deleted &&
                    "bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400",
                  item.gitStatus === GitStatus.Untracked &&
                    "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400",
                )}
              >
                {GitStatusBadgeLabel[item.gitStatus]}
              </span>
            )}

            {item.type === FileItemType.File && item.size !== undefined && !item.gitStatus && (
              <span className="text-xs text-muted-foreground opacity-0 group-hover:opacity-100 transition-opacity">
                {formatFileSize(item.size)}
              </span>
            )}

            {isLoading && <Loader2 className="h-3 w-3 animate-spin text-muted-foreground" />}

            {item.gitScope && (
              <button
                type="button"
                className={cn(
                  "inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-sm",
                  "opacity-0 transition-[opacity,color,background-color]",
                  "group-hover:opacity-100 group-focus-within:opacity-100 focus-visible:opacity-100",
                  "cursor-pointer hover:bg-background focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                  isUpdatingGitScope && "cursor-wait opacity-100",
                )}
                aria-label={`${item.gitScope === "staged" ? "Unstage" : "Stage"} ${item.type} ${item.name}`}
                title={item.gitScope === "staged" ? "Unstage changes" : "Stage changes"}
                disabled={isUpdatingGitScope}
                onClick={handleGitScopeChange}
                onDoubleClick={(event) => event.stopPropagation()}
              >
                {isUpdatingGitScope ? (
                  <Loader2 className="h-4 w-4 animate-spin" />
                ) : item.gitScope === "staged" ? (
                  <Minus className="h-4 w-4" />
                ) : (
                  <Plus className="h-4 w-4" />
                )}
              </button>
            )}
          </div>
        </ContextMenuTrigger>
        <ContextMenuContent>
          <ContextMenuItem onClick={handleDeleteRequest} variant="destructive">
            <Trash2 className="mr-2 h-4 w-4" />
            Delete
          </ContextMenuItem>
          {item.type === FileItemType.File && (
            <>
              <ContextMenuSeparator />
              <ContextMenuItem onClick={handleReset}>
                <RotateCcw className="mr-2 h-4 w-4" />
                Reset to HEAD
              </ContextMenuItem>
            </>
          )}
          {contextMenuItems && contextMenuItems.length > 0 ? (
            <>
              <ContextMenuSeparator />
              {contextMenuItems.map((menuItem) => (
                <ContextMenuItem
                  key={menuItem.id}
                  onSelect={() =>
                    menuItem.onSelect({
                      path: item.path,
                      name: item.name,
                      type: item.type,
                      directoryId: directoryId ?? null,
                    })
                  }
                >
                  {menuItem.icon}
                  {menuItem.title}
                </ContextMenuItem>
              ))}
            </>
          ) : null}
        </ContextMenuContent>
      </ContextMenu>

      <AlertDialog open={isDeleteDialogOpen} onOpenChange={setIsDeleteDialogOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>
              Delete {item.type === FileItemType.Directory ? "directory" : "file"}
            </AlertDialogTitle>
            <AlertDialogDescription>
              {item.type === FileItemType.Directory
                ? `Are you sure you want to delete the directory "${item.name}" and all its contents? This action cannot be undone.`
                : `Are you sure you want to delete "${item.name}"? This action cannot be undone.`}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={isDeleting}>Cancel</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={handleDelete} disabled={isDeleting}>
              {isDeleting ? "Deleting..." : "Delete"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      {error && (
        <div
          className="text-xs text-destructive px-2 py-1"
          style={{ paddingLeft: `${level * 12 + 32}px` }}
        >
          Error: {error}
        </div>
      )}

      {hasChildren && isExpanded && (
        <div role={isSelectable ? "group" : undefined}>
          {children.map((child) => (
            <FileTreeNode
              projectId={projectId}
              key={child.path}
              item={child}
              onFileSelect={onFileSelect}
              selectedPath={selectedPath}
              revealPath={revealPath}
              onFileRevealed={onFileRevealed}
              level={level + 1}
              diffMode={diffMode}
              recursiveMode={recursiveMode}
              defaultExpanded={defaultExpanded}
              onFileDeleted={onFileDeleted}
              onFileReset={onFileReset}
              onGitScopeChanged={onGitScopeChanged}
              contextMenuItems={contextMenuItems}
              refreshSignal={refreshSignal}
            />
          ))}
        </div>
      )}
    </div>
  );
}

export function ExplorerFileTree({
  projectId,
  rootItems,
  onFileSelect,
  selectedPath,
  onlyDiff,
  recursiveMode,
  onFileDeleted,
  onFileReset,
  contextMenuItems,
  refreshSignal,
}: {
  projectId: string;
  rootItems: FileItem[];
  onlyDiff: boolean;
  recursiveMode: boolean;
  onFileSelect: FileTreeSelectionHandler;
  selectedPath?: string | null;
  onFileDeleted: (filePath: string) => void;
  onFileReset: (filePath: string) => void;
  /** 右键菜单的扩展菜单项，非空时列在内置项之后。Extension context menu items listed after the built-in ones when non-empty. */
  contextMenuItems?: readonly ExtensionMenuItem<ExplorerContextMenuArgs>[];
  /** 变化时已展开的目录重新读取子项。Expanded directories reread their children when it changes. */
  refreshSignal?: number;
}) {
  const selection = useFileTreeSelection(selectedPath, onFileSelect);
  return (
    <div className="file-tree" role={selectedPath === undefined ? undefined : "tree"}>
      {rootItems.map((item: FileItem) => (
        <FileTreeNode
          projectId={projectId}
          key={item.path}
          item={item}
          {...selection}
          selectedPath={selectedPath}
          level={0}
          diffMode={onlyDiff}
          recursiveMode={recursiveMode}
          defaultExpanded={onlyDiff && recursiveMode}
          onFileDeleted={onFileDeleted}
          onFileReset={onFileReset}
          contextMenuItems={contextMenuItems}
          refreshSignal={refreshSignal}
        />
      ))}
    </div>
  );
}

function GitChangeGroupSection({
  projectId,
  group,
  onFileSelect,
  selectedPath,
  revealPath,
  onFileRevealed,
  onFileDeleted,
  onFileReset,
  onGitScopeChanged,
  contextMenuItems,
}: {
  projectId: string;
  group: GitChangeGroup;
  onFileSelect: FileTreeSelectionHandler;
  selectedPath?: string | null;
  revealPath?: string | null;
  onFileRevealed: () => void;
  onFileDeleted: (filePath: string) => void;
  onFileReset: (filePath: string) => void;
  onGitScopeChanged: (path: string, targetScope: GitDiffScope) => void;
  contextMenuItems?: readonly ExtensionMenuItem<ExplorerContextMenuArgs>[];
}) {
  const [isExpanded, setIsExpanded] = React.useState(true);
  React.useEffect(() => {
    if (revealPath) setIsExpanded(true);
  }, [revealPath]);

  return (
    <Collapsible open={isExpanded} onOpenChange={setIsExpanded}>
      <CollapsibleTrigger asChild>
        <button
          type="button"
          className="flex w-full items-center gap-1 rounded-sm px-2 py-1.5 text-left hover:bg-accent hover:text-accent-foreground"
        >
          {isExpanded ? (
            <ChevronDown className="h-4 w-4 shrink-0 text-muted-foreground" />
          ) : (
            <ChevronRight className="h-4 w-4 shrink-0 text-muted-foreground" />
          )}
          <span className="text-sm font-semibold">{group.label}</span>
          <span className="text-sm text-muted-foreground">{formatFileCount(group.fileCount)}</span>
        </button>
      </CollapsibleTrigger>
      <CollapsibleContent
        className="ml-4"
        role={selectedPath === undefined ? undefined : "tree"}
        aria-label={selectedPath === undefined ? undefined : group.label}
      >
        {group.items.map((item) => (
          <FileTreeNode
            projectId={projectId}
            key={`${group.scope}:${item.path}`}
            item={item}
            onFileSelect={onFileSelect}
            selectedPath={selectedPath}
            revealPath={isExpanded ? revealPath : null}
            onFileRevealed={onFileRevealed}
            level={0}
            diffMode={true}
            recursiveMode={true}
            defaultExpanded={true}
            onFileDeleted={onFileDeleted}
            onFileReset={onFileReset}
            onGitScopeChanged={onGitScopeChanged}
            contextMenuItems={contextMenuItems}
          />
        ))}
      </CollapsibleContent>
    </Collapsible>
  );
}

function containsFile(items: readonly FileTreeItem[], path: string): boolean {
  return items.some((item) => {
    if (item.type === FileItemType.File) return item.path === path;
    return path.startsWith(`${item.path}/`) && containsFile(item.children ?? [], path);
  });
}

export function ExplorerGitChangeTree({
  projectId,
  groups,
  onFileSelect,
  selectedPath,
  onFileDeleted,
  onFileReset,
  onGitScopeChanged,
  contextMenuItems,
}: {
  projectId: string;
  groups: GitChangeGroup[];
  onFileSelect: FileTreeSelectionHandler;
  selectedPath?: string | null;
  onFileDeleted: (filePath: string) => void;
  onFileReset: (filePath: string) => void;
  onGitScopeChanged: (path: string, targetScope: GitDiffScope) => void;
  contextMenuItems?: readonly ExtensionMenuItem<ExplorerContextMenuArgs>[];
}) {
  const [lastClicked, setLastClicked] = React.useState<{ path: string; scope: GitDiffScope }>();
  const selection = useFileTreeSelection(selectedPath, (path, scope, options) => {
    if (selectedPath !== undefined && scope) setLastClicked({ path, scope });
    onFileSelect(path, scope, options);
  });
  const selectedGroup = React.useMemo(() => {
    if (!selectedPath) return undefined;
    const matching = groups.filter((group) => containsFile(group.items, selectedPath));
    return (
      matching.find(
        (group) => lastClicked?.path === selectedPath && group.scope === lastClicked.scope,
      ) ?? matching[0]
    );
  }, [groups, lastClicked, selectedPath]);

  return (
    <div className="file-tree space-y-1">
      {groups.map((group) => (
        <GitChangeGroupSection
          projectId={projectId}
          key={group.scope}
          group={group}
          onFileSelect={selection.onFileSelect}
          onFileRevealed={selection.onFileRevealed}
          selectedPath={selectedPath === undefined || group === selectedGroup ? selectedPath : null}
          revealPath={group === selectedGroup ? selection.revealPath : null}
          onFileDeleted={onFileDeleted}
          onFileReset={onFileReset}
          onGitScopeChanged={onGitScopeChanged}
          contextMenuItems={contextMenuItems}
        />
      ))}
    </div>
  );
}
