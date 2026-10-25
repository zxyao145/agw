import * as React from "react";
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
  cn,
} from "@agw/components";
import { MoreHorizontal, RotateCw } from "lucide-react";

import { ProjectFileScopeContext, type ProjectFileScope } from "./project-file-scope";
import type { GitDiffResponse, GitDiffScope } from "../../../services/files";
import { DiffViewer } from "./diff-viewer";
import FileError from "./file-error";
import FileHeader from "./file-header";
import FileLoading from "./file-loading";
import { fileViewerFileType } from "./file-viewer-file-type";
import FileViewer from "./file-viewer";
import NoSelectedFile from "./no-selected-file";
import { LineComment } from "./types";
import UnChangedFile from "./unchanged-file";

/** 查看器报告的保存状态。The save status a viewer reports. */
export type FileViewerSaveStatus = "saved" | "unsaved" | "saving" | "changed-on-disk";

/** 匹配当前文件的查看器（Desktop extension 提供）。A viewer matching the current file (provided by Desktop extensions). */
export type FileViewerOption = {
  id: string;
  title: string;
  /** 有未保存修改。Has unsaved changes. */
  unsaved: boolean;
  /**
   * 选中时显示在文件名后的保存状态；null 时不显示。
   * The save status shown after the file name while the viewer is selected; nothing shows for null.
   */
  saveStatus: FileViewerSaveStatus | null;
};

const SOURCE_VIEWER_ID = "source";

const SAVE_STATUS_LABELS: Record<FileViewerSaveStatus, string> = {
  saved: "Saved",
  unsaved: "Unsaved changes",
  saving: "Saving…",
  "changed-on-disk": "Changed on disk",
};

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
   * 有值时文件名后的 "Changed on disk" 带 Reload 按钮，由查看器重新读取文件。
   * When set, "Changed on disk" after the file name carries a Reload button that makes the viewer read the file again.
   */
  onReloadFileViewerDocument?: (viewerId: string) => void;
  /**
   * 有值时选中 Source 后同一位置显示 "Reload file"，重新读取文件内容。
   * When set, selecting Source shows "Reload file" in the same place, which reads the file again.
   */
  onReloadSource?: () => void;
  /**
   * 显示在刷新按钮右侧的内容，例如查看器的来源说明；选中 Source 时对应切换前的查看器。
   * Shown to the right of the reload button, such as where the viewer comes from; with Source selected it describes
   * the viewer selected before the switch.
   */
  renderFileViewerInfo?: (viewerId: string) => React.ReactNode;
  /**
   * 用户为当前文件类型设置的默认查看器 id，或 "source"；匹配时初始选中它，非空时 "More" 菜单提供 "Reset default"。
   * The user's default viewer id for the file type, or "source"; selected initially when it matches, and a non-null value adds "Reset default" to the "More" menu.
   */
  defaultFileViewerId?: string | null;
  /**
   * 有值时切换按钮右侧显示 "More" 菜单：把当前选中的查看器（含 Source）设为默认，或传入 null 删除设置。
   * When set, a "More" menu follows the switch: set the selected viewer (Source included) as the default, or pass null to remove the setting.
   */
  onSetDefaultFileViewer?: (viewerId: string | null) => void;
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
  onReloadFileViewerDocument,
  onReloadSource,
  renderFileViewerInfo,
  defaultFileViewerId,
  onSetDefaultFileViewer,
}: FileContentProps) {
  // 切换文件时回到默认查看器。Switching files returns to the default viewer.
  const [choice, setChoice] = React.useState<{
    file: string | null;
    viewerId: string;
    /** 选中 Source 之前的查看器。The viewer selected before Source. */
    previousViewerId: string | null;
  } | null>(null);
  const viewers = onlyDiff || !renderFileViewer ? [] : fileViewers;
  const currentChoice = choice?.file === selectedFile ? choice : null;
  const chosen = currentChoice?.viewerId ?? defaultFileViewerId ?? null;
  const activeViewerId =
    viewers.length === 0
      ? SOURCE_VIEWER_ID
      : chosen === SOURCE_VIEWER_ID || viewers.some((viewer) => viewer.id === chosen)
        ? chosen!
        : viewers[0].id;
  const previousViewerId = currentChoice?.previousViewerId ?? null;
  const infoViewerId =
    activeViewerId !== SOURCE_VIEWER_ID
      ? activeViewerId
      : viewers.some((viewer) => viewer.id === previousViewerId)
        ? previousViewerId
        : (viewers[0]?.id ?? null);
  const choose = (viewerId: string) =>
    setChoice({
      file: selectedFile,
      viewerId,
      previousViewerId: activeViewerId === SOURCE_VIEWER_ID ? previousViewerId : activeViewerId,
    });
  // 修改默认查看器不切换正在显示的查看器。Changing the default keeps the viewer being shown.
  const setDefaultFileViewer = (viewerId: string | null) => {
    choose(activeViewerId);
    onSetDefaultFileViewer!(viewerId);
  };
  const fileType = fileViewerFileType((selectedFile ?? "").split(/[\\/]/).pop()!);
  const activeViewerTitle =
    activeViewerId === SOURCE_VIEWER_ID
      ? "Source"
      : (viewers.find((viewer) => viewer.id === activeViewerId)?.title ?? activeViewerId);
  const saveStatus = viewers.find((viewer) => viewer.id === activeViewerId)?.saveStatus ?? null;

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
                status={
                  saveStatus ? (
                    <span
                      data-file-save-status={saveStatus}
                      className="flex shrink-0 items-center gap-1 whitespace-nowrap text-xs text-muted-foreground"
                    >
                      {SAVE_STATUS_LABELS[saveStatus]}
                      {saveStatus === "changed-on-disk" && onReloadFileViewerDocument ? (
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          className="h-6 px-2 text-xs"
                          onClick={() => onReloadFileViewerDocument(activeViewerId)}
                        >
                          Reload
                        </Button>
                      ) : null}
                    </span>
                  ) : undefined
                }
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
                          {
                            id: SOURCE_VIEWER_ID,
                            title: "Source",
                            unsaved: false,
                            saveStatus: null,
                          },
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
                            onClick={() => choose(viewer.id)}
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
                      {onSetDefaultFileViewer ? (
                        <DropdownMenu>
                          <DropdownMenuTrigger asChild>
                            <Button
                              type="button"
                              variant="ghost"
                              size="icon"
                              className="size-6"
                              aria-label="More"
                            >
                              <MoreHorizontal className="size-3.5" />
                            </Button>
                          </DropdownMenuTrigger>
                          <DropdownMenuContent align="end">
                            <DropdownMenuItem
                              className="cursor-pointer"
                              onSelect={() => setDefaultFileViewer(activeViewerId)}
                            >
                              Open with {activeViewerTitle} by default for {fileType}
                            </DropdownMenuItem>
                            {defaultFileViewerId ? (
                              <DropdownMenuItem
                                className="cursor-pointer"
                                onSelect={() => setDefaultFileViewer(null)}
                              >
                                Reset default for {fileType}
                              </DropdownMenuItem>
                            ) : null}
                          </DropdownMenuContent>
                        </DropdownMenu>
                      ) : null}
                      {/* 选中 Source 时刷新按钮重新读取文件，位置不变。With Source selected the reload button reads the file again in place. */}
                      {activeViewerId === SOURCE_VIEWER_ID ? (
                        onReloadSource ? (
                          <Button
                            type="button"
                            variant="ghost"
                            size="icon"
                            className="size-6"
                            aria-label="Reload file"
                            title="Reload file"
                            onClick={onReloadSource}
                          >
                            <RotateCw className="size-3.5" />
                          </Button>
                        ) : null
                      ) : onReloadFileViewer ? (
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
                      {renderFileViewerInfo && infoViewerId !== null
                        ? renderFileViewerInfo(infoViewerId)
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
                // agw-file-source 供 Desktop 的工作区背景样式使用。agw-file-source serves Desktop's workspace backdrop styles.
                <div className="agw-file-source flex-1 min-h-0 overflow-y-auto agw-scrollbar">
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
