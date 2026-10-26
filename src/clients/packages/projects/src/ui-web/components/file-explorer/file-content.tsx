import * as React from "react";
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  ErrorBoundary,
  cn,
} from "@agw/components";
import { MoreHorizontal } from "lucide-react";

import { ProjectFileScopeContext, type ProjectFileScope } from "./project-file-scope";
import type { GitDiffResponse, GitDiffScope } from "../../../services/files";
import FileError from "./file-error";
import FileHeader from "./file-header";
import FileLoading from "./file-loading";
import { fileViewerFileType } from "./file-viewer-file-type";
import { FileViewerMenuEntries, type FileViewerMenuEntry } from "./file-viewer-menu";
import { GitDiffView } from "./git-diff-view";
import NoSelectedFile from "./no-selected-file";
import type { EditableSource } from "./source-document";
import { SourceView } from "./source-view";
import { LineComment } from "./types";

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
  /**
   * 选中时显示在 "More" 菜单中刷新项之后的条目，带 `menu` 的条目为子菜单。
   * Items shown in the "More" menu after the reload entry while the viewer is selected; an item with `menu` is a
   * submenu.
   */
  topItems: readonly FileViewerMenuEntry[];
  /**
   * 选中时显示在 "More" 菜单最后的条目，带 `menu` 的条目为子菜单。
   * Items shown at the end of the "More" menu while the viewer is selected; an item with `menu` is a submenu.
   */
  bottomItems: readonly FileViewerMenuEntry[];
};

const SOURCE_VIEWER_ID = "source";

/** Source 在 "More" 菜单中的条目 id。The ids of Source's "More" menu items. */
const SOURCE_ITEMS = { format: "format", wordWrap: "word-wrap", save: "save" } as const;

/** 查看器加载或运行失败（例如 Monaco 的 chunk 加载失败）。The viewer failed to load or run, e.g. Monaco's chunk. */
function CodeViewError({ error }: { error: Error }) {
  return <FileError message={error.message} />;
}

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
  /**
   * 有值时选中查看器后 "More" 菜单提供 "Reload view"。
   * When set, the "More" menu offers "Reload view" for the selected viewer.
   */
  onReloadFileViewer?: (viewerId: string) => void;
  /**
   * 有值时文件名后的 "Changed on disk" 带 Reload 按钮，由查看器重新读取文件。
   * When set, "Changed on disk" after the file name carries a Reload button that makes the viewer read the file again.
   */
  onReloadFileViewerDocument?: (viewerId: string) => void;
  /**
   * 有值时选中 Source 后 "More" 菜单在同一位置提供 "Reload file"，重新读取文件内容（Web）。
   * When set, selecting Source puts "Reload file" in the same place of the "More" menu, which reads the file again
   * (Web).
   */
  onReloadSource?: () => void;
  /**
   * Desktop 的可编辑 Source（设计文档第 5 节）；有值时 Source 可以编辑，"Reload file" 由文档会话重新读取。
   * Desktop's editable Source (design section 5); when set, Source edits the file and "Reload file" reads it again
   * through the document session.
   */
  editableSource?: EditableSource;
  /**
   * "More" 菜单顶部不能点击的一行，例如查看器的来源说明；选中 Source 时对应切换前的查看器。
   * The non-clickable row at the top of the "More" menu, such as where the viewer comes from; with Source selected it
   * describes the viewer selected before the switch.
   */
  renderFileViewerInfo?: (viewerId: string) => React.ReactNode;
  /**
   * 用户选择了查看器在 "More" 菜单中的条目；`entryId` 为子菜单中的项，条目没有子菜单时为 null。
   * The user chose one of the viewer's items in the "More" menu; `entryId` is the submenu entry, or null for an item
   * without a submenu.
   */
  onSelectFileViewerItem?: (viewerId: string, itemId: string, entryId: string | null) => void;
  /**
   * 用户为当前文件类型设置的默认查看器 id，或 "source"；匹配时初始选中它，非空时 "More" 菜单提供 "Reset default"。
   * The user's default viewer id for the file type, or "source"; selected initially when it matches, and a non-null value adds "Reset default" to the "More" menu.
   */
  defaultFileViewerId?: string | null;
  /**
   * 有值时 "More" 菜单可以把当前选中的查看器（含 Source）设为默认，或传入 null 删除设置。
   * When set, the "More" menu can set the selected viewer (Source included) as the default, or pass null to remove the setting.
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
  editableSource,
  renderFileViewerInfo,
  onSelectFileViewerItem,
  defaultFileViewerId,
  onSetDefaultFileViewer,
}: FileContentProps) {
  // Word wrap 在切换文件后保持。Word wrap stays across files.
  const [wordWrap, setWordWrap] = React.useState(false);
  // 编辑器能格式化时为执行格式化的函数。A function that formats while the editor can.
  const [formatSource, setFormatSource] = React.useState<(() => void) | null>(null);
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
  const activeViewer = viewers.find((viewer) => viewer.id === activeViewerId);
  const sourceSelected = activeViewerId === SOURCE_VIEWER_ID;
  // Only diff 显示 diff，Source 的编辑与条目都不出现。Only diff shows the diff without Source's editing and items.
  const sourceEditing = onlyDiff ? undefined : editableSource;
  const sourceDocument = sourceEditing?.document ?? null;
  const sourceShown =
    sourceDocument !== null &&
    sourceDocument.status !== "loading" &&
    sourceDocument.status !== "failed";
  const saveStatus = sourceSelected
    ? (sourceDocument?.saveStatus ?? null)
    : (activeViewer?.saveStatus ?? null);
  const reloadChangedDocument = sourceSelected
    ? sourceDocument && (() => sourceDocument.reloadChanged())
    : onReloadFileViewerDocument && (() => onReloadFileViewerDocument(activeViewerId));
  // 查看器的条目：没有子菜单的条目本身被选择时 entryId 为 null。The viewer's items: choosing an item without a submenu
  // reports a null entryId.
  const viewerItems = (items: readonly FileViewerMenuEntry[]) =>
    items.map((item) => (
      <FileViewerMenuEntries
        key={item.id}
        entries={[item]}
        onSelect={(entryId) =>
          onSelectFileViewerItem!(activeViewerId, item.id, item.menu ? entryId : null)
        }
      />
    ));
  // Source 的条目（设计文档第 4 节）：Format 与 Save 只在编辑器显示可编辑文档时出现；Word wrap 在 Web 与 Desktop 都有。
  // Source's items (design section 4): Format and Save appear only while the editor shows an editable document; Word
  // wrap exists in both Web and Desktop.
  const sourceEntries: FileViewerMenuEntry[] = onlyDiff
    ? []
    : sourceEditing === undefined
      ? [{ id: SOURCE_ITEMS.wordWrap, label: "Word wrap", checked: wordWrap }]
      : sourceShown
        ? [
            { id: SOURCE_ITEMS.format, label: "Format", disabled: formatSource === null },
            { id: SOURCE_ITEMS.wordWrap, label: "Word wrap", checked: wordWrap },
            { id: SOURCE_ITEMS.save, label: "Save", disabled: sourceDocument.status !== "dirty" },
          ]
        : [];
  const selectSourceItem = (itemId: string) => {
    if (itemId === SOURCE_ITEMS.format) formatSource?.();
    else if (itemId === SOURCE_ITEMS.wordWrap) setWordWrap((current) => !current);
    else if (itemId === SOURCE_ITEMS.save) sourceDocument?.save();
  };
  const sourceReload =
    sourceEditing === undefined
      ? onReloadSource
      : sourceDocument === null
        ? undefined
        : () => sourceDocument.reload();
  const reload = sourceSelected
    ? !onlyDiff && sourceReload && { label: "Reload file", run: sourceReload }
    : onReloadFileViewer && {
        label: "Reload view",
        run: () => onReloadFileViewer(activeViewerId),
      };
  const topItems = sourceSelected
    ? sourceEntries.length > 0
      ? [<FileViewerMenuEntries key="source" entries={sourceEntries} onSelect={selectSourceItem} />]
      : []
    : viewerItems(activeViewer?.topItems ?? []);
  // "More" 菜单依次为查看器说明、刷新与查看器的 top 条目、默认查看器设置、查看器的 bottom 条目；非空的组之间有分隔线。
  // The "More" menu holds the viewer info, then reloading with the viewer's top items, the default viewer setting and
  // the viewer's bottom items; non-empty groups are separated.
  const moreMenuGroups = [
    renderFileViewerInfo && infoViewerId !== null
      ? [
          <DropdownMenuLabel
            key="info"
            className="flex max-w-xs items-center gap-2 text-xs font-normal"
          >
            {renderFileViewerInfo(infoViewerId)}
          </DropdownMenuLabel>,
        ]
      : [],
    [
      ...(reload
        ? [
            <DropdownMenuItem key="reload" className="cursor-pointer" onSelect={reload.run}>
              {reload.label}
            </DropdownMenuItem>,
          ]
        : []),
      ...topItems,
    ],
    // 只有 Source 时没有可以选择的默认查看器。With Source alone there is no default viewer to choose.
    onSetDefaultFileViewer && viewers.length > 0
      ? [
          <DropdownMenuItem
            key="default"
            className="cursor-pointer"
            onSelect={() => setDefaultFileViewer(activeViewerId)}
          >
            Open with {activeViewerTitle} by default for {fileType}
          </DropdownMenuItem>,
          ...(defaultFileViewerId
            ? [
                <DropdownMenuItem
                  key="reset"
                  className="cursor-pointer"
                  onSelect={() => setDefaultFileViewer(null)}
                >
                  Reset default for {fileType}
                </DropdownMenuItem>,
              ]
            : []),
        ]
      : [],
    viewerItems(activeViewer?.bottomItems ?? []),
  ].filter((group) => group.length > 0);

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
                      {saveStatus === "changed-on-disk" && reloadChangedDocument ? (
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          className="h-6 px-2"
                          onClick={reloadChangedDocument}
                        >
                          Reload
                        </Button>
                      ) : null}
                    </span>
                  ) : undefined
                }
                actions={
                  viewers.length > 0 || moreMenuGroups.length > 0 ? (
                    <>
                      {viewers.length > 0 ? (
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
                              unsaved: sourceDocument?.unsaved ?? false,
                              saveStatus: null,
                              topItems: [],
                              bottomItems: [],
                            },
                          ].map((viewer) => (
                            <button
                              key={viewer.id}
                              type="button"
                              role="tab"
                              aria-selected={viewer.id === activeViewerId}
                              data-file-viewer-tab={viewer.id}
                              className={cn(
                                "cursor-pointer rounded-md px-2 py-0.5 text-sm text-muted-foreground hover:bg-accent hover:text-foreground",
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
                      ) : null}
                      {moreMenuGroups.length > 0 ? (
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
                            {moreMenuGroups.map((group, index) => (
                              <React.Fragment key={index}>
                                {index > 0 ? <DropdownMenuSeparator /> : null}
                                {group}
                              </React.Fragment>
                            ))}
                          </DropdownMenuContent>
                        </DropdownMenu>
                      ) : null}
                    </>
                  ) : undefined
                }
              />
              {activeViewerId !== SOURCE_VIEWER_ID ? (
                <div className="flex min-h-0 flex-1 flex-col">
                  {renderFileViewer!(activeViewerId)}
                </div>
              ) : (
                // Source 与 diff 铺满标题行以下的区域，Monaco 自己滚动。Source and the diff fill the area below the title row;
                // Monaco scrolls by itself.
                <div className="agw-file-source flex min-h-0 flex-1 flex-col overflow-hidden bg-background">
                  {isLoadingContent ? (
                    <FileLoading />
                  ) : contentError ? (
                    <FileError message={contentError} />
                  ) : (
                    <ErrorBoundary
                      resetKeys={[selectedFile, onlyDiff, diffScope]}
                      fallback={CodeViewError}
                    >
                      {onlyDiff && diffContentData ? (
                        <GitDiffView
                          diff={diffContentData}
                          filePath={selectedFile}
                          scope={diffScope}
                          comments={comments}
                          setComments={setComments}
                        />
                      ) : (
                        <SourceView
                          content={fileContent}
                          filePath={selectedFile}
                          editable={sourceEditing}
                          wordWrap={wordWrap}
                          onFormatChange={(format) => setFormatSource(() => format)}
                          comments={comments}
                          setComments={setComments}
                        />
                      )}
                    </ErrorBoundary>
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
