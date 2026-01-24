import { FileItem } from "@/api/files";

export type AdditionalProperties = Record<string, unknown>;

export const ClaudeCodeMessageType = {
  system: "system",
  assistant: "assistant",
  result: "result",
} as const;

export type ClaudeCodeMessageType = (typeof ClaudeCodeMessageType)[keyof typeof ClaudeCodeMessageType];

export interface InitMessageContent {
  claudeCodeVersion: string;
  permissionMode: PermissionMode;
  model: string;
  tools: string[];
  slashCommands: string[];
  agents: string[];
  skills: string[];
  plugins: string[];
  mcpServers: string[];
}

export const PermissionMode = {
  default: "default",
  acceptEdits: "acceptEdits",
  plan: "plan",
  bypassPermissions: "bypassPermissions",
} as const;

export type PermissionMode = (typeof PermissionMode)[keyof typeof PermissionMode];

// ============================================================================
// Chat Component Types
// ============================================================================

export interface ChatProps {
  messages: any[];
  input: string;
  setInput: (value: string) => void;
  isExecuting: boolean;
  workingDirectory: string;
  setWorkingDirectory: (value: string) => void;
  apiKey: string;
  setApiKey: (value: string) => void;
  apiBaseUrl: string;
  setApiBaseUrl: (value: string) => void;
  permissionMode: string;
  setPermissionMode: (value: string) => void;
  initContent: InitMessageContent | null;
  messagesEndRef: React.RefObject<HTMLDivElement>;
  onExecute: () => void;
  onExecuteWithComment: () => void;
  onClearSession: () => void;
  onKeyDown: (e: React.KeyboardEvent<HTMLTextAreaElement>) => void;
  processMessages: (msgs: any[]) => Array<
    | { type: "accordion"; messages: any[]; toolName: string }
    | { type: "normal"; message: any }
  >;
  createArr: (key: string, value: string[]) => React.ReactNode;
}

// ============================================================================
// File Explorer Component Types
// ============================================================================

export interface LineComment {
  id: string;
  isAfter: boolean;
  filePath: string;
  lineIndex: number;
  content: string;
  timestamp: Date;
}

export interface FileExplorerProps {
  rootDirectory: string;
  className?: string;
  onFileSelect?: (path: string) => void;
  comments: LineComment[];
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;
}

export interface FileTreeNodeProps {
  item: FileItem;
  onFileSelect?: (path: string) => void;
  level: number;
  diffMode: boolean;
  recursiveMode: boolean;
  onFileDeleted?: () => void;
  onFileReset?: () => void;
  defaultExpanded?: boolean;
}

export interface CodeViewerProps {
  content: string;
  filePath: string;
  comments: LineComment[];
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;
  isDiffView?: boolean;
  isOriginal?: boolean;
}

// ============================================================================
// Diff Viewer Component Types
// ============================================================================

export interface DiffLine {
  type: "add" | "remove" | "context" | "header";
  oldLineNum?: number;
  newLineNum?: number;
  content: string;
}

export interface DiffViewerProps {
  diff: string;
  className?: string;
  filePath?: string;
  comments?: LineComment[];
  setComments?: React.Dispatch<React.SetStateAction<LineComment[]>>;
}

export interface DiffLineRowProps {
  oldLine: DiffLine;
  newLine: DiffLine;
  index: number;
  comments: LineComment[];
  isHovered: boolean;
  isCommentActive: boolean;
  commentInput: string;
  activeSide: 'old' | 'new' | null;
  onHover: (index: number | null) => void;
  onToggleComment: (index: number | null, side: 'old' | 'new') => void;
  onCommentInputChange: (value: string) => void;
  onAddComment: (index: number, side: 'old' | 'new') => void;
  onDeleteComment: (id: string) => void;
}

// ============================================================================
// Settings Dialog Component Types
// ============================================================================

export interface SettingsDialogProps {
  workingDirectory: string;
  setWorkingDirectory: (value: string) => void;
  apiKey: string;
  setApiKey: (value: string) => void;
  apiBaseUrl: string;
  setApiBaseUrl: (value: string) => void;
  permissionMode: string;
  setPermissionMode: (value: string) => void;
}

