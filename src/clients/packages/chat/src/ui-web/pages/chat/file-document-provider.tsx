"use client";

import * as React from "react";
import { getApiErrorMessage } from "@agw/api";
import {
  getFileDiff,
  readFile,
  shouldShowFileDiff,
  type GitDiffResponse,
  type GitDiffScope,
  type ProjectDirectoryOption,
} from "@agw/projects";
import { useChatProject } from "./chat-project-provider";

export type FileDocumentProviderProps = {
  directoryId: string | null;
  path: string;
  diffScope: GitDiffScope | null;
  diffFileView: boolean;
  onDiffFileViewChange(diffFileView: boolean): void;
  readSourceContent: boolean;
  children: React.ReactNode;
};

export type FileDocumentContextValue = {
  directory: ProjectDirectoryOption | undefined;
  path: string;
  diffScope: GitDiffScope | null;
  diffFileView: boolean;
  setDiffFileView(value: boolean): void;
  fileContent: string;
  diffContentData: GitDiffResponse | null;
  isLoadingContent: boolean;
  contentError: string | null;
  reload(): void;
};
const FileDocumentContext = React.createContext<FileDocumentContextValue | null>(null);
export function useFileDocument() {
  const value = React.useContext(FileDocumentContext);
  if (!value) throw new Error("useFileDocument must be used inside FileDocumentProvider.");
  return value;
}

type FileContentState = Pick<
  FileDocumentContextValue,
  "fileContent" | "diffContentData" | "contentError" | "isLoadingContent"
>;
const emptyContent: FileContentState = {
  fileContent: "",
  diffContentData: null,
  contentError: null,
  isLoadingContent: false,
};
type FileRequest = Pick<FileDocumentProviderProps, "path" | "diffScope" | "directoryId"> & {
  projectId: string;
  showDiff: boolean;
};

async function fetchContent({
  projectId,
  path,
  diffScope,
  directoryId,
  showDiff,
}: FileRequest): Promise<FileContentState> {
  if (showDiff)
    return {
      ...emptyContent,
      diffContentData: await getFileDiff(
        projectId,
        path,
        diffScope ?? undefined,
        undefined,
        directoryId,
      ),
    };
  return { ...emptyContent, fileContent: await readFile(projectId, path, undefined, directoryId) };
}

function useFileRevision(directoryId: string | null, path: string) {
  const { subscribeFileChanges } = useChatProject();
  const [revision, reload] = React.useReducer((value: number) => value + 1, 0);
  React.useEffect(
    () =>
      subscribeFileChanges((change) => {
        if (change.directoryId === directoryId && change.path === path) reload();
      }),
    [subscribeFileChanges, directoryId, path],
  );
  return { revision, reload };
}

function useFileContent(
  request: FileRequest | null,
  revision: number,
  directoryAvailable: boolean,
) {
  const [content, setContent] = React.useState<FileContentState>({
    ...emptyContent,
    isLoadingContent: true,
  });
  React.useEffect(() => {
    let current = true;
    if (!directoryAvailable) {
      setContent({
        ...emptyContent,
        contentError: "The directory is no longer part of this Project.",
      });
      return;
    }
    if (!request) {
      setContent(emptyContent);
      return;
    }
    setContent({ ...emptyContent, isLoadingContent: true });
    // 选择仍然有效时开始读取。Start the read only if this selection survives the current commit.
    void Promise.resolve().then(async () => {
      if (!current) return;
      try {
        const result = await fetchContent(request);
        if (current) setContent(result);
      } catch (error) {
        if (current) {
          console.error("Failed to read the selected file.", error);
          setContent({ ...emptyContent, contentError: getApiErrorMessage(error) });
        }
      }
    });
    return () => {
      current = false;
    };
  }, [request, revision, directoryAvailable]);
  return content;
}

export function FileDocumentProvider(props: FileDocumentProviderProps) {
  const { projectDirectories, selectedProjectId: projectId, onlyDiff } = useChatProject();
  const { directoryId, path, diffScope, diffFileView, readSourceContent, onDiffFileViewChange } =
    props;
  const directory = projectDirectories.find((item) => item.id === directoryId);
  const directoryPath = directory?.path;
  const showDiff = shouldShowFileDiff(onlyDiff, diffFileView);
  const request = React.useMemo(() => {
    if (!projectId || directoryPath === undefined || !(showDiff || readSourceContent)) return null;
    return { projectId, directoryId, path, diffScope, showDiff };
  }, [projectId, directoryId, directoryPath, path, diffScope, showDiff, readSourceContent]);
  const { revision, reload } = useFileRevision(directoryId, path);
  const content = useFileContent(request, revision, directory !== undefined);
  const value = {
    ...content,
    directory,
    path,
    diffScope,
    diffFileView,
    setDiffFileView: onDiffFileViewChange,
    reload,
  };
  return <FileDocumentContext value={value}>{props.children}</FileDocumentContext>;
}
