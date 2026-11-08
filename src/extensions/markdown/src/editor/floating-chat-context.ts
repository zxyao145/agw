import type { FileViewerFile } from "@agw/extension-sdk";

/** 生成当前文件的 Chat 上下文。Builds Chat context for the current file. */
export function floatingChatContext(file: FileViewerFile): Record<string, string> {
  const url = new URL(file.uri);
  const segments = url.pathname.split("/").slice(1);
  const [uriProjectId, directoryKey, ...pathSegments] = segments;
  if (
    url.protocol !== "agw-cloud:" ||
    !url.host ||
    !uriProjectId ||
    !directoryKey ||
    pathSegments.length === 0
  ) {
    throw new Error(`Invalid Project file URI: ${file.uri}`);
  }

  const projectId = decodeURIComponent(uriProjectId);
  const decodedDirectoryKey = decodeURIComponent(directoryKey);
  if (projectId !== file.projectId) {
    throw new Error(`The file URI Project does not match the file Project: ${file.uri}`);
  }

  const projectRelativePath = pathSegments
    .map((segment) => decodeURIComponent(segment))
    .filter(Boolean)
    .join("/");
  if (projectRelativePath === "") throw new Error(`Invalid Project file URI: ${file.uri}`);

  return {
    projectId,
    ...(decodedDirectoryKey === "workspace" ? {} : { directoryId: decodedDirectoryKey }),
    projectRelativePath,
  };
}
