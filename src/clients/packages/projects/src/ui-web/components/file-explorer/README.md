# File Explorer

`file-explorer` is a reusable frontend module for browsing a workspace, viewing file content or git diff output, and attaching line comments to files or diff sides.

It is designed to be reusable across pages, but it is still intentionally aligned with the current Agw file API in [`@agw/projects`](../../../services/files.ts). In practice, this means it is a reusable "workspace/code explorer", not a storage-agnostic file manager.

## Exports

`index.tsx` currently exports:

```ts
export { default as Explorer } from "./explorer";
export { default as FileContent } from "./file-content";
export type { CommentSide, LineComment } from "./types";
```

## Main Components

### `Explorer`

Left-panel file tree for:

- listing workspace files
- switching between full listing and git diff mode
- grouping recursive diff results into `Staged` and `Unstaged` trees
- compacting single-child directory paths and showing recursive file counts
- selecting a file
- staging or unstaging a changed file or directory
- deleting a file or directory
- resetting a file back to git `HEAD`

`Explorer` is state-light. It owns tree loading state, but it delegates selected-file state and post-action behavior to the parent.

#### Props

```ts
{
  projectId: string;
  rootDirectory: string;
  onlyDiff: boolean;
  recursiveMode: boolean;
  onOnlyDiffChange?: (value: boolean) => void;
  onFileDeleted: (filePath: string) => void;
  onFileSelected: (filePath: string | null, scope?: GitDiffScope) => void;
  onFileReseted: (filePath: string | null) => void;
  onFileGitScopeChanged: (path: string, targetScope: GitDiffScope) => void;
}
```

### `FileContent`

Right-panel viewer for the currently selected file. It handles:

- empty state when no file is selected
- loading and error states
- plain file rendering
- diff rendering
- unchanged diff rendering
- line comment editing

`FileContent` does not fetch by itself. The parent provides the selected file, raw content, diff payload, and comments.
When `diffScope` is provided, its headers identify `HEAD → Staged` or
`Staged → Working Tree` content.

## Types

Shared file-explorer types live in [`types.ts`](./types.ts).

### `LineComment`

```ts
interface LineComment {
  id: string;
  side: CommentSide;
  diffScope?: GitDiffScope;
  filePath: string;
  lineNumber: number;
  content: string;
  timestamp: Date;
}
```

### `CommentSide`

```ts
const CommentSide = {
  Current: "current",
  Original: "original",
  Modified: "modified",
} as const;
```

Use `side` to describe where the comment was created:

- `CommentSide.Current`: comment on normal file content
- `CommentSide.Original`: comment on the original side of a diff
- `CommentSide.Modified`: comment on the modified side of a diff

`diffScope` records whether a diff comment was created in the `staged` or
`unstaged` view. Comments created in the normal current-file view omit it.

### Internal constants

The module also centralizes repeated string values for:

- `FileItemType`
- `GitStatus`
- `CommentSideLabel`
- `GitStatusBadgeLabel`
- `GitChangeGroup`
- `GitDiffScope`

These are intended to reduce local magic strings and keep rendering logic consistent.

## Data Flow

Typical parent flow:

1. Render `Explorer` with a project ID and an optional workspace label.
2. When a file is selected, fetch either raw content or git diff in the parent.
3. Pass the fetched data into `FileContent`.
4. Keep `comments` in parent state.
5. Translate `LineComment[]` into page-specific behavior such as review prompts, patch instructions, or audit notes.

This separation is intentional:

- `Explorer` owns directory-tree behavior.
- `FileContent` owns file presentation behavior.
- The page owns business logic.

## API Assumptions

This module currently depends on [`@agw/projects`](../../../services/files.ts) for:

- `listFiles`
- `readFile`
- `getFileDiff`
- `setFileStaged`
- `deleteFile`
- `resetFile`

Because of that, it assumes:

- paths are relative to the selected project's file-system root
- the selected project's workspace is a host-visible local directory
- file reset means reset to git `HEAD`
- `gitStagedStatus` and `gitUnstagedStatus` identify which diff groups contain a file
- `getFileDiff(projectId, path, scope)` uses `staged` or `unstaged` to load that group's diff

Remote storage must first be mounted or materialized as `Project.Workspace` by the host or container platform. Injecting a different frontend file-service interface alone would not give Git or external agent processes a usable working directory.

## Example Integration

```tsx
import * as React from "react";
import { Explorer, FileContent, type LineComment } from "@agw/projects";
import { getFileDiff, readFile, type GitDiffResponse, type GitDiffScope } from "@agw/projects";

export function ExampleFileExplorer({
  projectId,
  rootDirectory,
}: {
  projectId: string;
  rootDirectory: string;
}) {
  const [selectedFile, setSelectedFile] = React.useState<string | null>(null);
  const [selectedDiffScope, setSelectedDiffScope] = React.useState<GitDiffScope>();
  const [onlyDiff, setOnlyDiff] = React.useState(true);
  const [fileContent, setFileContent] = React.useState("");
  const [diffContentData, setDiffContentData] = React.useState<GitDiffResponse | null>(null);
  const [comments, setComments] = React.useState<LineComment[]>([]);
  const [isLoadingContent, setIsLoadingContent] = React.useState(false);
  const [contentError, setContentError] = React.useState<string | null>(null);

  const loadFileContent = React.useCallback(
    async (filePath: string, scope?: GitDiffScope) => {
      setIsLoadingContent(true);
      setContentError(null);

      try {
        if (onlyDiff) {
          const diff = await getFileDiff(projectId, filePath, scope);
          setDiffContentData(diff);
          setFileContent("");
          setSelectedDiffScope(scope);
        } else {
          const content = await readFile(projectId, filePath);
          setFileContent(content);
          setDiffContentData(null);
        }
      } catch (error) {
        setContentError(error instanceof Error ? error.message : "Failed to load file");
      } finally {
        setIsLoadingContent(false);
      }
    },
    [onlyDiff, projectId],
  );

  return (
    <div className="grid grid-cols-[320px_1fr] h-full">
      <Explorer
        projectId={projectId}
        rootDirectory={rootDirectory}
        onlyDiff={onlyDiff}
        recursiveMode={true}
        onOnlyDiffChange={setOnlyDiff}
        onFileDeleted={() => {}}
        onFileSelected={(filePath, scope) => {
          if (filePath) {
            setSelectedFile(filePath);
            void loadFileContent(filePath, scope);
          }
        }}
        onFileReseted={(filePath) => {
          if (filePath) {
            void loadFileContent(filePath, selectedDiffScope);
          }
        }}
        onFileGitScopeChanged={(path, targetScope) => {
          const directoryPrefix = `${path.replace(/\/+$/u, "")}/`;
          if (selectedFile === path || selectedFile?.startsWith(directoryPrefix)) {
            setSelectedDiffScope(targetScope);
          }
        }}
      />

      <FileContent
        selectedFile={selectedFile}
        isLoadingContent={isLoadingContent}
        contentError={contentError}
        onlyDiff={onlyDiff}
        diffContentData={diffContentData}
        comments={comments}
        setComments={setComments}
        fileContent={fileContent}
        diffScope={selectedDiffScope}
      />
    </div>
  );
}
```

## Directory Overview

- `explorer.tsx`: file tree container and data loading
- `explorer-file-tree.tsx`: recursive tree node rendering and actions
- `git-change-tree.ts`: staged/unstaged grouping and partially staged duplication
- `file-content.tsx`: selected file content shell
- `file-viewer.tsx`: plain text file viewer with line comments
- `diff-viewer.tsx`: split original/modified diff viewer
- `comment-section.tsx`: create, edit, and delete comments
- `types.ts`: shared module types and typed constants

## Current Boundaries

This module does not depend on a route page or import page-local types.

What it still does not try to abstract:

- backend transport for file operations
- git-specific concepts such as diff mode and reset-to-head
- page-specific handling of saved comments

That boundary is intentional.
