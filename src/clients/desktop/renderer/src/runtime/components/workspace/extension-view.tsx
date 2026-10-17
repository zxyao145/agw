"use client";

import * as React from "react";
import { Button } from "@agw/components";
import { AlertTriangle, RotateCw } from "lucide-react";

import type {
  ExtensionFailure,
  ExtensionHost,
  ExtensionHostSnapshot,
} from "../../extensions/extension-host";
import type { HostOverlayTracker } from "../../extensions/host-overlay-tracker";
import type { WorkspaceView } from "../../extensions/workspace-registry";
import { ExtensionViewSlot } from "./extension-view-slot";
import { WorkspaceIconView } from "./workspace-icon";

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
  tracker: HostOverlayTracker;
  snapshot: ExtensionHostSnapshot;
  view: WorkspaceView;
  /** 最近一次打开请求的序号与参数。The latest open request's sequence and params. */
  openRequest: { sequence: number; params: unknown } | undefined;
  /** 保留页面状态的视图切走后仍挂载但不再显示。A retained view stays mounted but inactive after switching away. */
  active?: boolean;
};

/** 一个 extension 视图：标题条 + 占位组件或错误面板。One extension view: the title bar plus the slot or an error panel. */
export function ExtensionView({
  host,
  tracker,
  snapshot,
  view,
  openRequest,
  active = true,
}: ExtensionViewProps) {
  const viewError = snapshot.viewErrors[view.id] ?? null;
  const failed = view.failure !== null;

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
      <ExtensionViewHeader
        view={view}
        onReload={() => {
          if (failed && view.extensionId) void host.reload(view.extensionId);
          else void host.reloadView(view.id);
        }}
      />
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
          tracker={tracker}
          frameId={frameId}
          snapshot={frame?.snapshot ?? null}
        />
      ) : (
        <div className="agw-extension-view-slot" />
      )}
    </div>
  );
}
