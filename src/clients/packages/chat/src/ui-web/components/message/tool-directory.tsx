import * as React from "react";

import { MessageContentType, type AiMessageContent } from "@agw/api";
import type { ProjectDirectoryOption } from "@agw/projects";

export const ToolDirectoriesContext = React.createContext<readonly ProjectDirectoryOption[]>([]);

const FILE_TOOL_NAMES = new Set([
  "file_access_read",
  "file_access_read_lines",
  "file_access_ls",
  "file_access_grep",
  "file_access_write",
  "file_access_delete",
  "file_access_replace",
  "file_access_replace_lines",
]);

export function ToolDirectoryInfo({ content }: { content: AiMessageContent }) {
  const directories = React.useContext(ToolDirectoriesContext);
  const toolName = content.additionalProperties?.toolName;
  if (
    content.type !== MessageContentType.FunctionCallContent ||
    typeof toolName !== "string" ||
    !FILE_TOOL_NAMES.has(toolName)
  ) {
    return null;
  }

  let args = content.content;
  if (typeof args === "string") {
    try {
      args = JSON.parse(args);
    } catch {
      return null;
    }
  }
  if (typeof args !== "object" || args === null || Array.isArray(args)) return null;

  const directoryId = (args as Record<string, unknown>).directoryId;
  const directory = directories.find((item) =>
    directoryId == null
      ? item.isPrimary
      : typeof directoryId === "string" && item.id?.toLowerCase() === directoryId.toLowerCase(),
  );
  if (!directory) return null;

  return (
    <div className="my-2 min-w-0 rounded-md border bg-muted/30 px-3 py-2 text-xs">
      <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
        <span className="text-muted-foreground">
          {directory.isPrimary ? "Primary directory" : "Directory"}
        </span>
        <span className="break-all font-medium">{directory.name}</span>
      </div>
      <div className="mt-1 break-all font-mono text-muted-foreground">{directory.path}</div>
    </div>
  );
}
