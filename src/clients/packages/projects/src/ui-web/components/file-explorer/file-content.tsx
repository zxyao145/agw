import * as React from "react";
import { Button, cn } from "@agw/components";
import { RotateCw } from "lucide-react";

import { ProjectFileScopeContext, type ProjectFileScope } from "./project-file-scope";
import type { GitDiffResponse, GitDiffScope } from "../../../services/files";
import { DiffViewer } from "./diff-viewer";
import FileError from "./file-error";
import FileHeader from "./file-header";
import FileLoading from "./file-loading";
import FileViewer from "./file-viewer";
import NoSelectedFile from "./no-selected-file";
import { LineComment } from "./types";
import UnChangedFile from "./unchanged-file";

/** 匹配当前文件的查看器（Desktop extension 提供）。A viewer matching the current file (provided by Desktop extensions). */
export type FileViewerOption = {
  id: string;
  title: string;
  /** 有未保存修改。Has unsaved changes. */
  unsaved: boolean;
};

const SOURCE_VIEWER_ID = "source";

interface FileContentProps extends ProjectFileScope {
  selectedFile: string | null;
  isLoadingContent: boolean;
  contentError: string | null;
  onlyDiff: boolean;
  diffContentData: GitDiffResponse | null;
  comments: LineComment[];
  setComments: React.Dispatch<React.SetStateAction<LineComment[]>>;
  fileContent: string;
  diffScope?: GitDiffScope;
  /**
   * 有值时文件名右侧可在查看器与源码之间切换，默认第一个查看器。
   * Viewers to switch between next to the file name, plus the source; the first is the default.
   */
  fileViewers?: readonly FileViewerOption[];
  renderFileViewer?: (viewerId: string) => React.ReactNode;
  /** 有值时选中查看器后切换按钮右侧显示 "Reload view"。When set, a selected viewer shows "Reload view" after the switch. */
  onReloadFileViewer?: (viewerId: string) => void;
  /**
   * 选中查看器时显示在 "Reload view" 右侧的内容，例如查看器的来源说明。
   * Shown to the right of "Reload view" while a viewer is selected, such as where the viewer comes from.
   */
  renderFileViewerInfo?: (viewerId: string) => React.ReactNode;
}

export default function FileContent({
  selectedFile,
  projectId,
  directoryId,
  directoryName,
  isLoadingContent,
  contentError,
  onlyDiff,
  diffContentData,
  comments,
  setComments,
  fileContent,
  diffScope,
  fileViewers = [],
  renderFileViewer,
  onReloadFileViewer,
  renderFileViewerInfo,
}: FileContentProps) {
  // 切换文件时回到默认查看器。Switching files returns to the default viewer.
  const [choice, setChoice] = React.useState<{ file: string | null; viewerId: string } | null>(
    null,
  );
  const viewers = onlyDiff || !renderFileViewer ? [] : fileViewers;
  const chosen = choice?.file === selectedFile ? choice.viewerId : null;
  const activeViewerId =
    viewers.length === 0
      ? SOURCE_VIEWER_ID
      : chosen === SOURCE_VIEWER_ID || viewers.some((viewer) => viewer.id === chosen)
        ? chosen!
        : viewers[0].id;

  return (
    <div className="flex h-full min-w-0 w-full flex-col">
      <ProjectFileScopeContext.Provider value={{ projectId, directoryId, directoryName }}>
        <div className="flex-1 min-h-0">
          {!selectedFile ? (
            NoSelectedFile()
          ) : (
            <div className="flex flex-col h-full min-h-0">
              <FileHeader
                file={directoryName ? `${directoryName}/ ${selectedFile}` : selectedFile}
                actions={
                  viewers.length > 0 ? (
                    <>
                      <div
                        role="tablist"
                        aria-label="File views"
                        className="flex items-center gap-1"
                      >
                        {[
                          ...viewers,
                          { id: SOURCE_VIEWER_ID, title: "Source", unsaved: false },
                        ].map((viewer) => (
                          <button
                            key={viewer.id}
                            type="button"
                            role="tab"
                            aria-selected={viewer.id === activeViewerId}
                            data-file-viewer-tab={viewer.id}
                            className={cn(
                              "rounded-md px-2 py-0.5 text-xs text-muted-foreground hover:bg-accent hover:text-foreground",
                              viewer.id === activeViewerId &&
                                "bg-accent font-medium text-foreground",
                            )}
                            onClick={() => setChoice({ file: selectedFile, viewerId: viewer.id })}
                          >
                            {viewer.unsaved ? (
                              <span aria-label="Unsaved changes" className="mr-1">
                                ●
                              </span>
                            ) : null}
                            {viewer.title}
                          </button>
                        ))}
                      </div>
                      {onReloadFileViewer && activeViewerId !== SOURCE_VIEWER_ID ? (
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          className="size-6"
                          aria-label="Reload view"
                          onClick={() => onReloadFileViewer(activeViewerId)}
                        >
                          <RotateCw className="size-3.5" />
                        </Button>
                      ) : null}
                      {renderFileViewerInfo && activeViewerId !== SOURCE_VIEWER_ID
                        ? renderFileViewerInfo(activeViewerId)
                        : null}
                    </>
                  ) : undefined
                }
              />
              {activeViewerId !== SOURCE_VIEWER_ID ? (
                <div className="flex min-h-0 flex-1 flex-col">
                  {renderFileViewer!(activeViewerId)}
                </div>
              ) : (
                <div className="flex-1 min-h-0 overflow-y-auto agw-scrollbar">
                  {isLoadingContent ? (
                    <FileLoading />
                  ) : contentError ? (
                    <FileError message={contentError} />
                  ) : onlyDiff && diffContentData ? (
                    diffContentData.unchanged ? (
                      <UnChangedFile
                        diffContentData={diffContentData}
                        selectedFile={selectedFile}
                        diffScope={diffScope}
                        comments={comments}
                        setComments={setComments}
                      />
                    ) : (
                      <DiffViewer
                        diff={diffContentData.diff}
                        filePath={selectedFile}
                        comments={comments}
                        setComments={setComments}
                        scope={diffScope}
                      />
                    )
                  ) : (
                    <FileViewer
                      content={fileContent}
                      filePath={selectedFile}
                      comments={comments}
                      setComments={setComments}
                      isDiffView={false}
                    />
                  )}
                </div>
              )}
            </div>
          )}
        </div>
      </ProjectFileScopeContext.Provider>
    </div>
  );
}
