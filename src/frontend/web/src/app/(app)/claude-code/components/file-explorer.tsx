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
  Plus,
  MessageSquare,
  X,
  Send,
  GripVertical,
  RotateCw,
  FolderOutput,
  FolderInput,
} from "lucide-react";
import { cn } from "@/lib/utils";
import {
  listFiles,
  readFile,
  getFileDiff,
  deleteFile,
  resetFile,
   type FileItem,
   type GitDiffResponse
  }  from "@/api/files";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { DiffViewer } from "./diff-viewer";
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from "@/components/ui/context-menu";
import { toast } from "sonner";
import {
  FileExplorerProps,
  FileTreeNodeProps,
  LineComment,
  CodeViewerProps,
} from "../types";

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

/**
 * Build a tree structure from a flat list of file paths
 * Used in recursive mode to display changed files in a directory tree
 */
function buildFileTree(items: FileItem[], rootPath: string): FileItem[] {
  // Normalize the root path
  const normalizedRoot = rootPath.replace(/\\/g, '/').replace(/\/$/, '');

  // Create a map to store directory items
  const dirMap = new Map<string, FileItem & { children: FileItem[] }>();

  // Sort items by path depth (shallow first) to ensure parents are created before children
  const sortedItems = [...items].sort((a, b) => {
    const aDepth = a.path.replace(/\\/g, '/').split('/').length;
    const bDepth = b.path.replace(/\\/g, '/').split('/').length;
    return aDepth - bDepth;
  });

  // Collect all git statuses for directory aggregation
  const fileStatusesMap = new Map<string, Set<string>>();

  sortedItems.forEach(item => {
    const normalizedPath = item.path.replace(/\\/g, '/');

    // Track statuses for each directory
    let currentPath = normalizedPath;
    while (currentPath !== normalizedRoot && currentPath !== '') {
      if (!fileStatusesMap.has(currentPath)) {
        fileStatusesMap.set(currentPath, new Set());
      }
      if (item.gitStatus) {
        fileStatusesMap.get(currentPath)!.add(item.gitStatus);
      }

      currentPath = currentPath.substring(0, currentPath.lastIndexOf('/'));
      if (currentPath === '') break;
    }
  });

  // Helper to get aggregated git status for a directory
  const getDirGitStatus = (path: string): "added" | "modified" | "deleted" | "untracked" | undefined => {
    const statuses = fileStatusesMap.get(path);
    if (!statuses || statuses.size === 0) return undefined;

    if (statuses.has('modified')) return 'modified';
    if (statuses.has('added')) return 'added';
    if (statuses.has('untracked')) return 'untracked';
    if (statuses.has('deleted')) return 'deleted';

    return undefined;
  };

  // Build tree structure
  sortedItems.forEach(item => {
    const normalizedPath = item.path.replace(/\\/g, '/');

    // Get parent path
    const parentPath = normalizedPath.substring(0, normalizedPath.lastIndexOf('/'));

    // Ensure all parent directories exist
    let currentPath = parentPath;
    const pathsToCreate: string[] = [];

    while (currentPath !== normalizedRoot && currentPath !== '') {
      if (!dirMap.has(currentPath)) {
        pathsToCreate.unshift(currentPath);
      }
      currentPath = currentPath.substring(0, currentPath.lastIndexOf('/'));
      if (currentPath === '') break;
    }

    // Create missing parent directories
    pathsToCreate.forEach(path => {
      const dirName = path.substring(path.lastIndexOf('/') + 1);
      dirMap.set(path, {
        name: dirName,
        path: path,
        type: 'directory',
        gitStatus: getDirGitStatus(path),
        children: []
      });
    });

    // Add file to its parent directory
    if (item.type === 'file') {
      if (parentPath === normalizedRoot || parentPath === '') {
        // File at root level - will be added later
      } else if (dirMap.has(parentPath)) {
        dirMap.get(parentPath)!.children.push(item);
      }
    } else if (item.type === 'directory') {
      // Create or update directory
      if (!dirMap.has(normalizedPath)) {
        dirMap.set(normalizedPath, {
          ...item,
          children: []
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
      const parentPath = path.substring(0, path.lastIndexOf('/')) || normalizedRoot;
      const normalizedParent = parentPath.replace(/\\/g, '/');

      if ((dirPath === null && normalizedParent === normalizedRoot) ||
          (dirPath !== null && normalizedParent === dirPath)) {
        levelMap.set(path, { ...dir });
      }
    });

    // Process files at this level
    items.forEach(item => {
      const normalizedPath = item.path.replace(/\\/g, '/');
      const parentPath = normalizedPath.substring(0, normalizedPath.lastIndexOf('/')) || normalizedRoot;
      const normalizedParent = parentPath.replace(/\\/g, '/');

      if ((dirPath === null && normalizedParent === normalizedRoot) ||
          (dirPath !== null && normalizedParent === dirPath)) {
        const key = `file-${normalizedPath}`;
        levelMap.set(key, { ...item });
      }
    });

    // Build and sort items at this level
    levelMap.forEach(item => {
      if (item.type === 'directory') {
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
        return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
      }
      return a.type === 'directory' ? -1 : 1;
    });
  };

  return buildTree(null, items);
}

function CodeViewer({ content, filePath, comments, setComments, isDiffView, isOriginal }: CodeViewerProps) {
  const [activeCommentLine, setActiveCommentLine] = React.useState<number | null>(null);
  const [commentInput, setCommentInput] = React.useState("");
  const [hoveredLine, setHoveredLine] = React.useState<number | null>(null);

  const lines = React.useMemo(() => content.split("\n"), [content]);
  const lineNumberWidth = React.useMemo(() => String(lines.length).length, [lines.length]);

  const getCommentsForLine = React.useCallback(
    (lineNumber: number) => comments.filter((c) => c.lineIndex === lineNumber),
    [comments]
  );

  const handleAddComment = (lineNumber: number) => {
    if (!commentInput.trim()) return;

    const newComment: LineComment = {
      id: `${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
      // In diff view: Original side = isAfter=false, Modified side = isAfter=true
      // In normal view: isAfter=true
      isAfter: isDiffView ? !isOriginal : true,
      filePath: filePath,
      lineIndex: lineNumber,
      content: commentInput.trim(),
      timestamp: new Date(),
    };

    setComments((prev) => [...prev, newComment]);
    setCommentInput("");
    setActiveCommentLine(null);
  };

  const handleDeleteComment = (commentId: string) => {
    setComments((prev) => prev.filter((c) => c.id !== commentId));
  };

  return (
    <div className="font-mono text-sm">
      {lines.map((line, index) => {
        const lineNumber = index + 1;
        const lineComments = getCommentsForLine(lineNumber);
        const isHovered = hoveredLine === lineNumber;
        const isCommentActive = activeCommentLine === lineNumber;

        return (
          <div key={index}>
            <div
              className={cn(
                "flex group hover:bg-muted/50 transition-colors",
                isCommentActive && "bg-muted/50"
              )}
              onMouseEnter={() => setHoveredLine(lineNumber)}
              onMouseLeave={() => setHoveredLine(null)}
            >
              {/* Line number with add comment button */}
              <div className="flex items-center shrink-0 select-none sticky left-0 bg-inherit">
                <div className="w-6 flex items-center justify-center">
                  {(isHovered || isCommentActive) && (
                    <button
                      onClick={() => setActiveCommentLine(isCommentActive ? null : lineNumber)}
                      className={cn(
                        "w-5 h-5 flex items-center justify-center rounded text-primary-foreground transition-colors",
                        isCommentActive
                          ? "bg-primary"
                          : "bg-blue-500 hover:bg-blue-600 cursor-pointer"
                      )}
                      title="Add comment"
                    >
                      <Plus className="h-3 w-3" />
                    </button>
                  )}
                </div>
                <div
                  className="px-3 py-0.5 text-right text-muted-foreground border-r border-border"
                  style={{ minWidth: `${lineNumberWidth + 2}ch` }}
                >
                  {lineNumber}
                </div>
              </div>

              {/* Code content */}
              <pre className="flex-1 px-4 py-0.5 whitespace-pre overflow-x-auto">
                <code>{line || " "}</code>
              </pre>

              {/* Comment indicator */}
              {lineComments.length > 0 && !isCommentActive && (
                <div className="flex items-center pr-2">
                  <button
                    onClick={() => setActiveCommentLine(lineNumber)}
                    className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
                  >
                    <MessageSquare className="h-3 w-3" />
                    <span>{lineComments.length}</span>
                  </button>
                </div>
              )}
            </div>

            {/* Comment section */}
            {(isCommentActive || (lineComments.length > 0 && activeCommentLine === lineNumber)) && (
              <div className="border-y border-border bg-muted/30 ml-6" style={{ marginLeft: `${lineNumberWidth + 5}ch` }}>
                {/* Existing comments */}
                {lineComments.map((comment) => (
                  <div key={comment.id} className="p-3 border-b border-border last:border-b-0">
                    <div className="flex items-start justify-between gap-2">
                      <div className="flex-1">
                        <div className="flex items-center gap-2 mb-1">
                          <span className="text-xs text-muted-foreground">
                            {comment.timestamp.toLocaleTimeString()}
                          </span>
                        </div>
                        <p className="text-sm whitespace-pre-wrap">{comment.content}</p>
                      </div>
                      <button
                        onClick={() => handleDeleteComment(comment.id)}
                        className="text-muted-foreground hover:text-destructive p-1 cursor-pointer"
                        title="Delete comment"
                      >
                        <X className="h-3 w-3" />
                      </button>
                    </div>
                  </div>
                ))}

                {/* Add comment input */}
                {isCommentActive && (
                  <div className="p-3">
                    <Textarea
                      value={commentInput}
                      onChange={(e) => setCommentInput(e.target.value)}
                      placeholder="Write a comment..."
                      className="min-h-20 text-sm resize-none"
                      autoFocus
                      onKeyDown={(e) => {
                        if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
                          handleAddComment(lineNumber);
                        }
                        if (e.key === "Escape") {
                          setActiveCommentLine(null);
                          setCommentInput("");
                        }
                      }}
                    />
                    <div className="flex items-center justify-between mt-2">
                      <span className="text-xs text-muted-foreground">
                        Ctrl+Enter to submit, Esc to cancel
                      </span>
                      <div className="flex gap-2">
                        <Button
                          variant="ghost"
                          size="sm"
                          onClick={() => {
                            setActiveCommentLine(null);
                            setCommentInput("");
                          }}
                        >
                          Cancel
                        </Button>
                        <Button
                          size="sm"
                          onClick={() => handleAddComment(lineNumber)}
                          disabled={!commentInput.trim()}
                        >
                          <Send className="h-3 w-3 mr-1" />
                          Comment
                        </Button>
                      </div>
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}

function FileTreeNode({ item, onFileSelect, level, diffMode, recursiveMode, onFileDeleted, onFileReset, defaultExpanded }: FileTreeNodeProps) {
  const [isExpanded, setIsExpanded] = React.useState(
    defaultExpanded && item.type === 'directory' && (item.children?.length ?? 0) > 0
  );
  // Use item.children if available (pre-built tree), otherwise empty for lazy loading
  const initialChildren = item.children || [];
  const [children, setChildren] = React.useState<FileItem[]>(initialChildren);
  const [isLoading, setIsLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  // Update children when item.children change (for pre-built trees)
  React.useEffect(() => {
    if (item.children) {
      setChildren(item.children);
    }
  }, [item.children]);

  const loadChildren = async () => {
    if (item.type !== "directory" || children.length > 0) return;

    setIsLoading(true);
    setError(null);

    try {
      const data = await listFiles(item.path, diffMode, recursiveMode);
      setChildren(data.items || []);
    } catch (err) {
      console.error("Error loading directory:", err);
      setError((err as Error).message);
    } finally {
      setIsLoading(false);
    }
  };

  const handleToggle = () => {
    if (item.type === "directory") {
      setIsExpanded(!isExpanded);
      if (!isExpanded && children.length === 0 && !item.children) {
        loadChildren();
      }
    }
  };

  const handleClick = () => {
    if (item.type === "file" && onFileSelect) {
      onFileSelect(item.path);
    } else {
      handleToggle();
    }
  };

  const handleDelete = async (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();

    const confirmMessage = item.type === "directory"
      ? `Are you sure you want to delete the directory "${item.name}" and all its contents?`
      : `Are you sure you want to delete "${item.name}"?`;

    if (!confirm(confirmMessage)) {
      return;
    }

    try {
      const result = await deleteFile(item.path);
      if (result.success) {
        toast.success(result.message);
        onFileDeleted?.();
      } else {
        toast.error(result.message || "Failed to delete");
      }
    } catch (err) {
      console.error("Error deleting:", err);
      toast.error((err as Error).message);
    }
  };

  const handleReset = async (e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();

    if (item.type !== "file") {
      toast.error("Can only reset files, not directories");
      return;
    }

    try {
      const result = await resetFile(item.path);
      if (result.success) {
        toast.success(result.message);
        onFileReset?.();
      } else {
        toast.info(result.message || "No changes to reset");
      }
    } catch (err) {
      console.error("Error resetting:", err);
      toast.error((err as Error).message);
    }
  };

  const FileIcon = item.type === "file" ? getFileIcon(item.name) : null;
  const FolderIcon = isExpanded ? FolderOpen : Folder;
  const ChevronIcon = isExpanded ? ChevronDown : ChevronRight;

  const hasChildren = item.type === "directory" && children.length > 0;

  return (
    <div>
      <ContextMenu>
        <ContextMenuTrigger asChild>
          <div
            className={cn(
              "flex items-center gap-1 py-1 px-2 hover:bg-accent hover:text-accent-foreground cursor-pointer rounded-sm group",
              "transition-colors"
            )}
            style={{ paddingLeft: `${level * 12 + 8}px` }}
            onClick={handleClick}
          >
            {item.type === "directory" && (
              <ChevronIcon className="h-4 w-4 shrink-0 text-muted-foreground" />
            )}
            {item.type === "file" && <div className="w-4" />}

            {item.type === "directory" ? (
              <FolderIcon className="h-4 w-4 shrink-0 text-blue-500" />
            ) : (
              FileIcon && (
                <FileIcon className="h-4 w-4 shrink-0 text-muted-foreground" />
              )
            )}

            <span className={cn(
              "text-sm truncate flex-1",
              item.gitStatus === "deleted" && "line-through text-muted-foreground"
            )}>{item.name}</span>

            {/* Git status indicator */}
            {item.gitStatus && (
              <span className={cn(
                "text-[10px] font-medium px-1.5 py-0.5 rounded",
                item.gitStatus === "added" && "bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400",
                item.gitStatus === "modified" && "bg-yellow-100 text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400",
                item.gitStatus === "deleted" && "bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400",
                item.gitStatus === "untracked" && "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400"
              )}>
                {item.gitStatus === "added" && "A"}
                {item.gitStatus === "modified" && "M"}
                {item.gitStatus === "deleted" && "D"}
                {item.gitStatus === "untracked" && "U"}
              </span>
            )}

            {item.type === "file" && item.size !== undefined && !item.gitStatus && (
              <span className="text-xs text-muted-foreground opacity-0 group-hover:opacity-100 transition-opacity">
                {formatFileSize(item.size)}
              </span>
            )}

            {isLoading && (
              <Loader2 className="h-3 w-3 animate-spin text-muted-foreground" />
            )}
          </div>
        </ContextMenuTrigger>
        <ContextMenuContent>
          <ContextMenuItem onClick={handleDelete} variant="destructive">
            <Trash2 className="mr-2 h-4 w-4" />
            Delete
          </ContextMenuItem>
          {item.type === "file" && (
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

export function FileExplorer({
  rootDirectory,
  className,
  onFileSelect,
  comments,
  setComments,
}: FileExplorerProps) {
  const [rootItems, setRootItems] = React.useState<FileItem[]>([]);
  const [isLoading, setIsLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [selectedFile, setSelectedFile] = React.useState<string | null>(null);
  const [fileContent, setFileContent] = React.useState<string>("");
  const [isLoadingContent, setIsLoadingContent] = React.useState(false);
  const [contentError, setContentError] = React.useState<string | null>(null);
  const [showFileExplorer, setShowFileExplorer] = React.useState(true);
  const [onlyDiff, setOnlyDiff] = React.useState(true);
  const [recursiveMode, setRecursiveMode] = React.useState(true);
  const [onlyModifiedData, setOnlyModifiedData] = React.useState<GitDiffResponse | null>(null);
  const [panelWidth, setPanelWidth] = React.useState(320);
  const [isResizing, setIsResizing] = React.useState(false);
  const resizeRef = React.useRef<HTMLDivElement>(null);

  // Handle resize
  React.useEffect(() => {
    const handleMouseMove = (e: MouseEvent) => {
      if (!isResizing) return;

      const containerRect = resizeRef.current?.parentElement?.getBoundingClientRect();
      if (!containerRect) return;

      const newWidth = e.clientX - containerRect.left;
      // Clamp between min 200px and max 600px
      setPanelWidth(Math.max(200, Math.min(600, newWidth)));
    };

    const handleMouseUp = () => {
      setIsResizing(false);
      document.body.style.cursor = "";
      document.body.style.userSelect = "";
    };

    if (isResizing) {
      document.body.style.cursor = "col-resize";
      document.body.style.userSelect = "none";
      document.addEventListener("mousemove", handleMouseMove);
      document.addEventListener("mouseup", handleMouseUp);
    }

    return () => {
      document.removeEventListener("mousemove", handleMouseMove);
      document.removeEventListener("mouseup", handleMouseUp);
    };
  }, [isResizing]);

  const loadRootDirectory = React.useCallback(async () => {
    if (!rootDirectory || !rootDirectory.trim()) {
      setRootItems([]);
      setError("No working directory specified");
      return;
    }

    setIsLoading(true);
    setError(null);

    try {
      const data = await listFiles(rootDirectory, onlyDiff, recursiveMode);
      let items = data.items || [];

      // In recursive mode with diff, build a tree structure
      if (recursiveMode && onlyDiff) {
        items = buildFileTree(items, rootDirectory);
      }

      setRootItems(items);
    } catch (err) {
      console.error("Error loading root directory:", err);
      setError((err as Error).message);
      setRootItems([]);
    } finally {
      setIsLoading(false);
    }
  }, [rootDirectory, onlyDiff, recursiveMode]);

  React.useEffect(() => {
    loadRootDirectory();
  }, [loadRootDirectory]);

  const loadFileContent = React.useCallback(
    async (filePath: string) => {
      setIsLoadingContent(true);
      setContentError(null);
      setOnlyModifiedData(null);

      try {
        if (onlyDiff) {
          const diff = await getFileDiff(filePath);
          setOnlyModifiedData(diff);
          setFileContent("");
          setSelectedFile(filePath);
        } else {
          const content = await readFile(filePath);
          setFileContent(content);
          setOnlyModifiedData(null);
          setSelectedFile(filePath);
        }
      } catch (err) {
        console.error("Error loading file:", err);
        setContentError((err as Error).message);
        setFileContent("");
        setOnlyModifiedData(null);
      } finally {
        setIsLoadingContent(false);
      }
    },
    [onlyDiff]
  );

  const handleFileSelect = React.useCallback(
    (path: string) => {
      loadFileContent(path);
      onFileSelect?.(path);
    },
    [loadFileContent, onFileSelect]
  );

  const handleFileDeleted = React.useCallback(() => {
    // Reload the directory after deletion
    loadRootDirectory();
    // Clear selected file if it was deleted
    setSelectedFile(null);
    setFileContent("");
    setOnlyModifiedData(null);
  }, [loadRootDirectory]);

  const handleFileReset = React.useCallback(() => {
    // Reload current file if it was reset
    if (selectedFile) {
      loadFileContent(selectedFile);
    }
    // Reload directory to update modified status
    loadRootDirectory();
  }, [selectedFile, loadFileContent, loadRootDirectory]);

  // Reload current file when diff mode changes
  React.useEffect(() => {
    if (selectedFile) {
      loadFileContent(selectedFile);
    }
  }, [onlyDiff]); // Only depend on diffMode, not loadFileContent to avoid infinite loop

  return (
    <>
      <div className="flex items-center gap-4">
        <Button
          variant="ghost"
          className="cursor-pointer"
          size="sm"
          onClick={() => setShowFileExplorer(!showFileExplorer)}
          title={showFileExplorer ? "Hide file explorer" : "Show file explorer"}
        >
          {
            showFileExplorer 
            ? <FolderOutput className="h-4 w-4" />
            : <FolderInput className="h-4 w-4" />
          }
        </Button>
        <div className="flex items-center gap-2">
          <Switch
            id="diff-mode"
            checked={onlyDiff}
            onCheckedChange={setOnlyDiff}
          />
          <Label htmlFor="diff-mode" className="text-sm cursor-pointer">
            Diff
          </Label>
        </div>
        {/* {onlyModified && (
          <div className="flex items-center gap-2">
            <Switch
              id="recursive-mode"
              checked={recursiveMode}
              onCheckedChange={setRecursiveMode}
            />
            <Label htmlFor="recursive-mode" className="text-sm cursor-pointer" title="Show all changed files recursively">
              Recursive
            </Label>
          </div>
        )} */}
      </div>
      <div className="flex-1 flex overflow-hidden">
        {!showFileExplorer ? null : (
          <div
            ref={resizeRef}
            className="shrink-0 flex"
            style={{ width: panelWidth }}
          >
            <div className={cn("border rounded-lg flex flex-col flex-1 overflow-hidden", className)}>
              <div className="border-b px-3 py-2 bg-muted/50">
                <div className="flex items-center justify-between">
                  <h3 className="text-sm font-medium">File Explorer</h3>
                  {isLoading ? (
                    <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" />
                  ):(

                    <RotateCw onClick={loadRootDirectory} className="h-4 w-4 cursor-pointer"/>
                  )}
                </div>
                {rootDirectory && (
                  <p className="text-xs text-muted-foreground truncate mt-1">
                    {rootDirectory}
                  </p>
                )}
              </div>

              <div className="flex-1 h-100 overflow-auto">
                {/* <div className="p-2  max-h-[calc(100vh-186px)] overflow-y-scroll"> */}
                <div className="p-2">
                  {error && (
                    <div className="text-sm text-destructive p-2 bg-destructive/10 rounded">
                      {error}
                    </div>
                  )}

                  {!error && !isLoading && rootItems.length === 0 && (
                    <div className="text-sm text-muted-foreground p-2 text-center">
                      {rootDirectory
                        ? "Directory is empty or cannot be accessed"
                        : "Set a working directory in settings to browse files"}
                    </div>
                  )}

                  {!error && rootItems.length > 0 && (
                    <div className="file-tree">
                      {rootItems.map((item: FileItem) => (
                        <FileTreeNode
                          key={item.path}
                          item={item}
                          onFileSelect={handleFileSelect}
                          level={0}
                          diffMode={onlyDiff}
                          recursiveMode={recursiveMode}
                          defaultExpanded={onlyDiff && recursiveMode}
                          onFileDeleted={handleFileDeleted}
                          onFileReset={handleFileReset}
                        />
                      ))}
                    </div>
                  )}
                </div>
              </div>
            </div>

            {/* Resize handle */}
            <div
              className={cn(
                "w-1 cursor-col-resize flex items-center justify-center bg-primary/20 transition-colors group",
                isResizing && "bg-primary/30"
              )}
              onMouseDown={(e) => {
                e.preventDefault();
                setIsResizing(true);
              }}
            >
              <GripVertical className="h-4 w-4 text-muted-foreground opacity-0 group-hover:opacity-100 transition-opacity" />
            </div>
          </div>
        )}
        <div className="flex-1 overflow-hidden flex flex-col min-h-full">
          {!selectedFile ? (
            <div className="flex items-center justify-center h-full text-muted-foreground">
              <div className="text-center">
                <FileText className="h-12 w-12 mx-auto mb-3 opacity-50" />
                <p className="text-sm">Select a file to view its contents</p>
              </div>
            </div>
          ) : (
            <div className="flex flex-col h-full">
              <div className="border-b px-4 py-2 bg-muted/30">
                <div className="flex items-center gap-2">
                  <File className="h-4 w-4 text-muted-foreground" />
                  <span className="text-sm font-medium truncate">
                    {selectedFile.split("/").pop()}
                  </span>
                </div>
                <p className="text-xs text-muted-foreground truncate mt-0.5">
                  {selectedFile}
                </p>
              </div>

              <div className="flex-1 overflow-auto">
                {isLoadingContent ? (
                  <div className="flex items-center justify-center h-full">
                    <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
                  </div>
                ) : contentError ? (
                  <div className="p-4">
                    <div className="text-sm text-destructive p-3 bg-destructive/10 rounded">
                      Error loading file: {contentError}
                    </div>
                  </div>
                ) : onlyDiff && onlyModifiedData ? (
                  onlyModifiedData.unchanged ? (
                    <div className="p-4">
                      <div className="text-sm text-muted-foreground p-3 bg-muted/50 rounded mb-4">
                        {onlyModifiedData.message || "No changes detected"}
                      </div>
                      {onlyModifiedData.originalContent && (
                        <CodeViewer
                          content={onlyModifiedData.originalContent}
                          filePath={selectedFile}
                          comments={comments}
                          setComments={setComments}
                          isDiffView={true}
                          isOriginal={true}
                        />
                      )}
                    </div>
                  ) : (
                    <DiffViewer
                      diff={onlyModifiedData.diff}
                      filePath={selectedFile}
                      comments={comments}
                      setComments={setComments}
                    />
                  )
                ) : (
                  <CodeViewer
                   content={fileContent}
                   filePath={selectedFile}
                   comments={comments}
                   setComments={setComments}
                   isDiffView={false}
                   />
                )}
              </div>
            </div>
          )}
        </div>
      </div>
    </>
  );
}
