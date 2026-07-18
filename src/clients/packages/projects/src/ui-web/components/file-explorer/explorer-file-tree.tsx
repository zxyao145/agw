"use client";

import * as React from "react";
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
  Trash2,
  RotateCcw,
} from "lucide-react";
import { cn } from "@agw/components";
import { deleteFile, listFiles, resetFile, type FileItem } from "../../../services/files";
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
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from "@agw/components";
import { toast } from "sonner";
import { FileItemType, FileTreeNodeProps, GitStatus, GitStatusBadgeLabel } from "./types";

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

function FileTreeNode({
  projectId,
  item,
  onFileSelect,
  level,
  diffMode,
  recursiveMode,
  onFileDeleted,
  onFileReset,
  defaultExpanded,
}: FileTreeNodeProps) {
  const [isExpanded, setIsExpanded] = React.useState(
    defaultExpanded && item.type === FileItemType.Directory && (item.children?.length ?? 0) > 0,
  );
  // Use item.children if available (pre-built tree), otherwise empty for lazy loading
  const initialChildren = item.children || [];
  const [children, setChildren] = React.useState<FileItem[]>(initialChildren);
  const [isLoading, setIsLoading] = React.useState(false);
  const [isDeleteDialogOpen, setIsDeleteDialogOpen] = React.useState(false);
  const [isDeleting, setIsDeleting] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  // Update children when item.children change (for pre-built trees)
  React.useEffect(() => {
    if (item.children) {
      setChildren(item.children);
    }
  }, [item.children]);

  const loadChildren = async () => {
    if (item.type !== FileItemType.Directory || children.length > 0) return;

    setIsLoading(true);
    setError(null);

    try {
      const data = await listFiles(projectId, item.path, diffMode, recursiveMode);
      setChildren(data.items || []);
    } catch (err) {
      console.error("Error loading directory:", err);
      setError((err as Error).message);
    } finally {
      setIsLoading(false);
    }
  };

  const handleToggle = () => {
    if (item.type === FileItemType.Directory) {
      setIsExpanded(!isExpanded);
      if (!isExpanded && children.length === 0 && !item.children) {
        loadChildren();
      }
    }
  };

  const handleClick = () => {
    if (item.type === FileItemType.File && onFileSelect) {
      onFileSelect(item.path);
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
      const result = await deleteFile(projectId, item.path);
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
      const result = await resetFile(projectId, item.path);
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

  const FileIcon = item.type === FileItemType.File ? getFileIcon(item.name) : null;
  const FolderIcon = isExpanded ? FolderOpen : Folder;
  const ChevronIcon = isExpanded ? ChevronDown : ChevronRight;

  const hasChildren = item.type === FileItemType.Directory && children.length > 0;

  return (
    <div>
      <ContextMenu>
        <ContextMenuTrigger asChild>
          <div
            className={cn(
              "flex items-center gap-1 py-1 px-2 hover:bg-accent hover:text-accent-foreground cursor-pointer rounded-sm group",
              "transition-colors",
            )}
            style={{ paddingLeft: `${level * 12 + 8}px` }}
            onClick={handleClick}
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

            {/* Git status indicator */}
            {item.gitStatus && (
              <span
                className={cn(
                  "text-[10px] font-medium px-1.5 py-0.5 rounded",
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
        <div>
          {children.map((child) => (
            <FileTreeNode
              projectId={projectId}
              key={child.path}
              item={child}
              onFileSelect={onFileSelect}
              level={level + 1}
              diffMode={diffMode}
              recursiveMode={recursiveMode}
              defaultExpanded={defaultExpanded}
              onFileDeleted={onFileDeleted}
              onFileReset={onFileReset}
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
  onlyDiff,
  recursiveMode,
  onFileDeleted,
  onFileReset,
}: {
  projectId: string;
  rootItems: FileItem[];
  onlyDiff: boolean;
  recursiveMode: boolean;
  onFileSelect: (filePath: string) => void;
  onFileDeleted: (filePath: string) => void;
  onFileReset: (filePath: string) => void;
}) {
  return (
    <div className="file-tree">
      {rootItems.map((item: FileItem) => (
        <FileTreeNode
          projectId={projectId}
          key={item.path}
          item={item}
          onFileSelect={onFileSelect}
          level={0}
          diffMode={onlyDiff}
          recursiveMode={recursiveMode}
          defaultExpanded={onlyDiff && recursiveMode}
          onFileDeleted={onFileDeleted}
          onFileReset={onFileReset}
        />
      ))}
    </div>
  );
}
