import { FileItem, GitDiffResponse } from "@/api/files";

export const CommentSide = {
  Current: "current",
  Original: "original",
  Modified: "modified",
} as const;

export type CommentSide = (typeof CommentSide)[keyof typeof CommentSide];

export const FileItemType = {
  File: "file",
  Directory: "directory",
} as const;

export type FileItemType = FileItem["type"];

export const GitStatus = {
  Added: "added",
  Modified: "modified",
  Deleted: "deleted",
  Untracked: "untracked",
} as const;

export type GitStatus = NonNullable<FileItem["gitStatus"]>;

export const CommentSideLabel: Record<CommentSide, string> = {
  [CommentSide.Current]: "current",
  [CommentSide.Original]: "original",
  [CommentSide.Modified]: "modified",
};

export const GitStatusBadgeLabel: Record<GitStatus, string> = {
  [GitStatus.Added]: "A",
  [GitStatus.Modified]: "M",
  [GitStatus.Deleted]: "D",
  [GitStatus.Untracked]: "U",
};

export interface LineComment {
  id: string;
  side: CommentSide;
  filePath: string;
  lineNumber: number;
  content: string;
  timestamp: Date;
}

export interface FileTreeNodeProps {
  item: FileItem;
  onFileSelect?: (path: string) => void;
  level: number;
  diffMode: boolean;
  recursiveMode: boolean;
  onFileDeleted?: (filePath: string) => void;
  onFileReset?: (filePath: string) => void;
  defaultExpanded?: boolean;
}

export interface CodeViewerProps {
  content: string;
  filePath: string;
  comments: LineComment[];
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;
  isDiffView?: boolean;
  commentSide?: CommentSide;
}

export interface DiffViewerProps {
  diff: string;
  className?: string;
  filePath?: string;
  comments?: LineComment[];
  setComments?: React.Dispatch<React.SetStateAction<LineComment[]>>;
}

export interface UnChangedFileProps {
  diffContentData: GitDiffResponse;
  selectedFile: string;
  comments: LineComment[];
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;
}
