"use client";

import * as React from "react";
import { Button, Popover, PopoverContent, PopoverTrigger } from "@agw/components";
import type { FileViewerFile } from "@agw/extension-sdk";
import { AlertTriangle, Info, RotateCw } from "lucide-react";

import type {
  ExtensionFailure,
  ExtensionHost,
  ExtensionHostSnapshot,
} from "../../extensions/extension-host";
import type { ExtensionLayoutSampler } from "../../extensions/extension-layout-sampler";
import type { WorkspaceView } from "../../extensions/workspace-registry";
import { ExtensionViewSlot } from "./extension-view-slot";
import { WorkspaceIconView } from "./workspace-icon";

/**
 * 重新加载视图；extension 已失败时重新加载 extension。视图有未保存修改时先由用户确认放弃。
 * Reloads the view, or the extension once it failed. With unsaved changes the user confirms discarding them first.
 */
export async function reloadExtensionView(host: ExtensionHost, view: WorkspaceView): Promise<void> {
  if (!(await host.confirmDiscardUnsaved([view.id]))) return;
  if (view.failure !== null && view.extensionId) await host.reload(view.extensionId);
  else await host.reloadView(view.id);
}

/** 宿主绘制的标题条，标明这块内容由哪个 extension 提供。The host-drawn title bar naming the extension behind the view. */
export function ExtensionViewHeader({
  view,
  onReload,
}: {
  view: Pick<WorkspaceView, "icon" | "title" | "extensionName">;
  onReload(): void;
}) {
  return (
    <div className="agw-extension-view-header">
      <WorkspaceIconView icon={view.icon} />
      <span className="truncate font-medium text-foreground">{view.title}</span>
      <span className="truncate">{view.extensionName}</span>
      <span className="flex-1" />
      <Button
        type="button"
        variant="ghost"
        size="icon"
        className="size-6"
        aria-label="Reload view"
        onClick={onReload}
      >
        <RotateCw className="size-3.5" />
      </Button>
    </div>
  );
}

/**
 * 文件查看器的 Info 按钮：弹出层显示标题条的内容，标明查看器由哪个 extension 提供。
 * A file viewer's Info button: its popup shows the title bar content, naming the extension behind the viewer.
 */
export function ExtensionViewInfo({
  view,
}: {
  view: Pick<WorkspaceView, "icon" | "title" | "extensionName">;
}) {
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button type="button" variant="ghost" size="icon" className="size-6" aria-label="View info">
          <Info className="size-3.5" />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="flex w-auto max-w-xs items-center gap-2 p-3 text-xs">
        <WorkspaceIconView icon={view.icon} className="shrink-0" />
        <span className="truncate font-medium">{view.title}</span>
        <span className="truncate text-muted-foreground">{view.extensionName}</span>
      </PopoverContent>
    </Popover>
  );
}

function ErrorPanel({
  extensionName,
  viewTitle,
  error,
  actionLabel,
  onAction,
}: {
  extensionName: string;
  viewTitle: string;
  error: ExtensionFailure;
  actionLabel: string;
  onAction(): void;
}) {
  return (
    <div
      role="alert"
      className="flex min-h-0 flex-1 flex-col items-center justify-center gap-3 p-6 text-center"
    >
      <AlertTriangle className="size-6 text-destructive" aria-hidden="true" />
      <div className="space-y-1">
        <p className="text-sm font-medium">
          {extensionName} · {viewTitle}
        </p>
        <p className="max-w-md text-xs text-muted-foreground">
          {error.code}: {error.message}
        </p>
      </div>
      <Button type="button" variant="outline" size="sm" onClick={onAction}>
        {actionLabel}
      </Button>
    </div>
  );
}

/** extension 进入 failed 后视图位置显示的面板。The panel shown in a view's place after its extension failed. */
export function ExtensionErrorView({
  view,
  error,
  onReloadExtension,
}: {
  view: WorkspaceView;
  error: ExtensionFailure;
  onReloadExtension(): void;
}) {
  return (
    <ErrorPanel
      extensionName={view.extensionName ?? ""}
      viewTitle={view.title}
      error={error}
      actionLabel="Reload extension"
      onAction={onReloadExtension}
    />
  );
}

export type ExtensionViewProps = {
  host: ExtensionHost;
  layout: ExtensionLayoutSampler;
  snapshot: ExtensionHostSnapshot;
  view: WorkspaceView;
  /** 最近一次打开请求的序号与参数。The latest open request's sequence and params. */
  openRequest: { sequence: number; params: unknown } | undefined;
  /** 保留页面状态的视图切走后仍挂载但不再显示。A retained view stays mounted but inactive after switching away. */
  active?: boolean;
  /** 文件查看器显示的文件。The file a file viewer shows. */
  file?: FileViewerFile;
  /**
   * 显示宿主标题条；文件查看器的标题条内容与 "Reload view" 在文件标题行。
   * Shows the host title bar; file viewers have its content and "Reload view" in the file title row.
   */
  showHeader?: boolean;
};

/** 一个 extension 视图：标题条 + 占位组件或错误面板。One extension view: the title bar plus the slot or an error panel. */
export function ExtensionView({
  host,
  layout,
  snapshot,
  view,
  openRequest,
  active = true,
  file,
  showHeader = true,
}: ExtensionViewProps) {
  const viewError = snapshot.viewErrors[view.id] ?? null;
  const failed = view.failure !== null;

  // 先交给宿主文件，再获取视图，新 frame 的 onLoad 才带着文件。Hand over the file before acquiring, so onLoad carries it.
  const fileUri = file?.uri;
  const fileName = file?.name;
  const fileProjectId = file?.projectId;
  React.useEffect(() => {
    if (fileUri === undefined || fileName === undefined || fileProjectId === undefined) return;
    host.setFileViewerFile(view.id, { uri: fileUri, name: fileName, projectId: fileProjectId });
  }, [host, view.id, fileUri, fileName, fileProjectId]);

  React.useEffect(() => {
    if (!active || failed || viewError) return;
    return host.acquireView(view.id);
  }, [host, view.id, active, failed, viewError]);

  const firstRequest = React.useRef(openRequest?.sequence);
  React.useEffect(() => {
    if (!openRequest || openRequest.sequence === firstRequest.current) return;
    firstRequest.current = openRequest.sequence;
    host.reopenView(view.id, openRequest.params);
  }, [host, view.id, openRequest]);

  const frameId = host.frameIdOfView(view.id);
  const frame = snapshot.extensions
    .flatMap((extension) => extension.frames)
    .find((item) => item.frameId === frameId);

  return (
    <div className="flex h-full min-h-0 w-full flex-col" data-extension-view={view.id}>
      {showHeader ? (
        <ExtensionViewHeader view={view} onReload={() => void reloadExtensionView(host, view)} />
      ) : null}
      {failed && view.failure && view.extensionId ? (
        <ExtensionErrorView
          view={view}
          error={view.failure}
          onReloadExtension={() => void host.reload(view.extensionId!)}
        />
      ) : viewError ? (
        <ErrorPanel
          extensionName={view.extensionName ?? ""}
          viewTitle={view.title}
          error={viewError}
          actionLabel="Reload view"
          onAction={() => void host.reloadView(view.id)}
        />
      ) : frameId ? (
        <ExtensionViewSlot
          host={host}
          layout={layout}
          frameId={frameId}
          snapshot={frame?.snapshot ?? null}
        />
      ) : (
        <div className="agw-extension-view-slot" />
      )}
    </div>
  );
}
