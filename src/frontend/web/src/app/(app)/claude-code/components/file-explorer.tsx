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
} from "lucide-react";
import { cn } from "@/lib/utils";

interface FileItem {
  name: string;
  path: string;
  type: "file" | "directory";
  size?: number;
  modifiedTime?: string;
}

interface FileExplorerProps {
  rootDirectory: string;
  className?: string;
  onFileSelect?: (path: string) => void;
}

interface FileTreeNodeProps {
  item: FileItem;
  onFileSelect?: (path: string) => void;
  level: number;
}

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

function FileTreeNode({ item, onFileSelect, level }: FileTreeNodeProps) {
  const [isExpanded, setIsExpanded] = React.useState(false);
  const [children, setChildren] = React.useState<FileItem[]>([]);
  const [isLoading, setIsLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  const loadChildren = async () => {
    if (item.type !== "directory" || children.length > 0) return;

    setIsLoading(true);
    setError(null);

    try {
      const response = await fetch(
        `/api/files/list?path=${encodeURIComponent(item.path)}`
      );

      if (!response.ok) {
        throw new Error(`Failed to load directory: ${response.statusText}`);
      }

      const data = await response.json();
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
      if (!isExpanded && children.length === 0) {
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

  const FileIcon = item.type === "file" ? getFileIcon(item.name) : null;
  const FolderIcon = isExpanded ? FolderOpen : Folder;
  const ChevronIcon = isExpanded ? ChevronDown : ChevronRight;

  return (
    <div>
      <div
        className={cn(
          "flex items-center gap-1 py-1 px-2 hover:bg-accent hover:text-accent-foreground cursor-pointer rounded-sm group",
          "transition-colors"
        )}
        style={{ paddingLeft: `${level * 12 + 8}px` }}
        onClick={handleClick}
      >
        {item.type === "directory" && (
          <ChevronIcon className="h-4 w-4 flex-shrink-0 text-muted-foreground" />
        )}
        {item.type === "file" && <div className="w-4" />}

        {item.type === "directory" ? (
          <FolderIcon className="h-4 w-4 flex-shrink-0 text-blue-500" />
        ) : (
          FileIcon && (
            <FileIcon className="h-4 w-4 flex-shrink-0 text-muted-foreground" />
          )
        )}

        <span className="text-sm truncate flex-1">{item.name}</span>

        {item.type === "file" && item.size !== undefined && (
          <span className="text-xs text-muted-foreground opacity-0 group-hover:opacity-100 transition-opacity">
            {formatFileSize(item.size)}
          </span>
        )}

        {isLoading && (
          <Loader2 className="h-3 w-3 animate-spin text-muted-foreground" />
        )}
      </div>

      {error && (
        <div
          className="text-xs text-destructive px-2 py-1"
          style={{ paddingLeft: `${level * 12 + 32}px` }}
        >
          Error: {error}
        </div>
      )}

      {item.type === "directory" && isExpanded && children.length > 0 && (
        <div>
          {children.map((child) => (
            <FileTreeNode
              key={child.path}
              item={child}
              onFileSelect={onFileSelect}
              level={level + 1}
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
}: FileExplorerProps) {
  const [rootItems, setRootItems] = React.useState<FileItem[]>([]);
  const [isLoading, setIsLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  const loadRootDirectory = React.useCallback(async () => {
    if (!rootDirectory || !rootDirectory.trim()) {
      setRootItems([]);
      setError("No working directory specified");
      return;
    }

    setIsLoading(true);
    setError(null);

    try {
      const response = await fetch(
        `/api/files/list?path=${encodeURIComponent(rootDirectory)}`
      );

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(
          errorData.error || `Failed to load directory: ${response.statusText}`
        );
      }

      const data = await response.json();
      setRootItems(data.items || []);
    } catch (err) {
      console.error("Error loading root directory:", err);
      setError((err as Error).message);
      setRootItems([]);
    } finally {
      setIsLoading(false);
    }
  }, [rootDirectory]);

  React.useEffect(() => {
    loadRootDirectory();
  }, [loadRootDirectory]);

  return (
    <div className="flex-1 flex overflow-hidden">
      <div className="w-80 border-r shrink-0">
        <div className={cn("border rounded-lg flex flex-col", className)}>
          <div className="border-b px-3 py-2 bg-muted/50">
            <div className="flex items-center justify-between">
              <h3 className="text-sm font-medium">File Explorer</h3>
              {isLoading && (
                <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" />
              )}
            </div>
            {rootDirectory && (
              <p className="text-xs text-muted-foreground truncate mt-1">
                {rootDirectory}
              </p>
            )}
          </div>

          <div className="flex-1 h-100 overflow-auto">
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
                <div>
                  {rootItems.map((item) => (
                    <FileTreeNode
                      key={item.path}
                      item={item}
                      onFileSelect={onFileSelect}
                      level={0}
                    />
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
      <div className="flex-1 overflow-y-auto p-4 space-y-4">// files</div>
    </div>
  );
}
