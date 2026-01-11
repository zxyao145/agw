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
import { listFiles, readFile, type FileItem } from "@/api/files";

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
      const data = await listFiles(item.path);
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
  const [selectedFile, setSelectedFile] = React.useState<string | null>(null);
  const [fileContent, setFileContent] = React.useState<string>("");
  const [isLoadingContent, setIsLoadingContent] = React.useState(false);
  const [contentError, setContentError] = React.useState<string | null>(null);

  const loadRootDirectory = React.useCallback(async () => {
    if (!rootDirectory || !rootDirectory.trim()) {
      setRootItems([]);
      setError("No working directory specified");
      return;
    }

    setIsLoading(true);
    setError(null);

    try {
      const data = await listFiles(rootDirectory);
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

  const loadFileContent = React.useCallback(async (filePath: string) => {
    setIsLoadingContent(true);
    setContentError(null);

    try {
      const content = await readFile(filePath);
      setFileContent(content);
      setSelectedFile(filePath);
    } catch (err) {
      console.error("Error reading file:", err);
      setContentError((err as Error).message);
      setFileContent("");
    } finally {
      setIsLoadingContent(false);
    }
  }, []);

  const handleFileSelect = React.useCallback(
    (path: string) => {
      loadFileContent(path);
      onFileSelect?.(path);
    },
    [loadFileContent, onFileSelect]
  );

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
                      onFileSelect={handleFileSelect}
                      level={0}
                    />
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
      <div className="flex-1 overflow-hidden flex flex-col">
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
              ) : (
                <pre className="p-4 text-sm font-mono whitespace-pre-wrap break-words">
                  <code>{fileContent}</code>
                </pre>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
