import { createExtensionError } from "@agw/extension-sdk";
import type { JsonValue } from "@desktop/shared/contracts";
import {
  BUILT_IN_VIEWS,
  DEFAULT_SIDEBAR_VIEW_ID,
  FULL_WORKSPACE_VIEWS,
  isBuiltInViewId,
} from "./workspace-ids";
import type { WorkspaceRegistry, WorkspaceViewLocation } from "./workspace-registry";
import type {
  ExtensionTabOptions,
  TabGroupScope,
  WorkspaceTabsStore,
} from "./workspace-tabs-store";

export type ViewOpenRequest = { params: unknown; sequence: number };

export type WorkspaceLayoutState = {
  sidebarVisible: boolean;
  activeSidebarViewId: string;
  /** 显示中的全屏视图。没有全屏视图时显示标签组。The full-workspace view, or null to show the tabs. */
  fullWorkspaceViewId: string | null;
  /** 侧栏与全屏视图最近一次打开时的参数与序号。The latest open params and sequence for sidebar and full views. */
  openRequests: Readonly<Record<string, ViewOpenRequest>>;
};

export const DEFAULT_WORKSPACE_LAYOUT: WorkspaceLayoutState = {
  sidebarVisible: true,
  activeSidebarViewId: DEFAULT_SIDEBAR_VIEW_ID,
  fullWorkspaceViewId: null,
  openRequests: {},
};

/** 三区布局状态，保存在内存中，进出 Settings 不丢失。Three-region layout state kept in memory across Settings. */
export class WorkspaceLayoutStore {
  private state: WorkspaceLayoutState = DEFAULT_WORKSPACE_LAYOUT;
  private sequence = 0;
  private sidebarVisibleBeforeFullView = true;
  private navigateToChat: (() => void) | null = null;
  private readonly listeners = new Set<() => void>();

  public constructor(
    private readonly registry: WorkspaceRegistry,
    private readonly tabs: WorkspaceTabsStore,
  ) {
    registry.subscribe(() => this.fallBackToDefaults());
  }

  public getSnapshot = (): WorkspaceLayoutState => this.state;

  public getServerSnapshot = (): WorkspaceLayoutState => DEFAULT_WORKSPACE_LAYOUT;

  public subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  /** 外壳提供：不在对话路由时导航到 `/desktop/chat/`。Provided by the shell: navigates to `/desktop/chat/` off the chat route. */
  public setNavigateToChat(navigate: (() => void) | null): void {
    this.navigateToChat = navigate;
  }

  /** 该视图正在显示且侧栏可见时收起侧栏，否则显示并切换。Hides the sidebar when this view shows; otherwise shows and switches. */
  public toggleSidebarView(id: string): void {
    this.assertView(id, "sidebar");
    if (this.state.sidebarVisible && this.state.activeSidebarViewId === id) {
      this.update({ sidebarVisible: false });
      return;
    }
    this.showSidebarView(id);
  }

  public showSidebarView(id: string): void {
    this.assertView(id, "sidebar");
    this.update({
      fullWorkspaceViewId: null,
      sidebarVisible: true,
      activeSidebarViewId: id,
      openRequests: this.withRequest(id, {}),
    });
  }

  public setSidebarVisible(visible: boolean): void {
    if (visible && this.state.fullWorkspaceViewId !== null) {
      this.showSidebarView(this.state.activeSidebarViewId);
      return;
    }
    this.update({ sidebarVisible: visible });
  }

  public openMainView(
    id: string,
    params: JsonValue = {},
    options: Partial<ExtensionTabOptions> = {},
  ): void | Promise<void> {
    this.assertView(id, "main");
    if (id === BUILT_IN_VIEWS.chat || id === BUILT_IN_VIEWS.file) {
      throw createExtensionError(
        "ERR_UNSUPPORTED_OPERATION",
        "Conversations and files open as tabs from the sidebar or workspace.openFile.",
      );
    }
    if (isBuiltInViewId(id) && Object.keys(options).length > 0) {
      throw createExtensionError(
        "ERR_INVALID_ARGUMENT",
        "Built-in views do not accept tab options.",
      );
    }
    this.navigateToChat?.();
    if (FULL_WORKSPACE_VIEWS.has(id)) {
      if (this.state.fullWorkspaceViewId === null) {
        this.sidebarVisibleBeforeFullView = this.state.sidebarVisible;
      }
      this.update({
        fullWorkspaceViewId: id,
        sidebarVisible: false,
        openRequests: this.withRequest(id, params),
      });
      return;
    }
    return this.openTabWhenReady(() => {
      this.leaveFullWorkspaceView();
      if (id === BUILT_IN_VIEWS.jobs) this.tabs.openJobs();
      else
        this.tabs.openExtensionView(id, params, {
          key: options.key ?? null,
          title: options.title ?? null,
          preview: options.preview ?? false,
        });
    });
  }

  /** 从 Settings 导航回来时，打开请求跟随即将读取的 Project。On return from Settings, the request follows the Project being loaded. */
  private openTabWhenReady(open: () => void): void | Promise<void> {
    const current = this.tabs.getSnapshot();
    if (current.error) throw current.error;
    if (current.group) {
      open();
      return;
    }
    if (!current.scope && !this.navigateToChat) throw new Error("The tab group is not loaded.");

    let scope = current.scope;
    const changed = () =>
      createExtensionError("ERR_CONNECTION_CHANGED", "The active Project changed.");
    return new Promise<TabGroupScope>((resolve, reject) => {
      const unsubscribe = this.tabs.subscribe(() => {
        const next = this.tabs.getSnapshot();
        if (scope && scope !== next.scope) {
          unsubscribe();
          reject(changed());
          return;
        }
        scope ??= next.scope;
        if (next.error) {
          unsubscribe();
          reject(next.error);
        } else if (scope && next.group) {
          unsubscribe();
          resolve(scope);
        }
      });
    }).then((loaded) => {
      if (loaded !== this.tabs.getSnapshot().scope) throw changed();
      open();
    });
  }

  public leaveFullWorkspaceView(): void {
    if (this.state.fullWorkspaceViewId === null) return;
    this.update({ fullWorkspaceViewId: null, sidebarVisible: this.sidebarVisibleBeforeFullView });
  }

  private assertView(id: string, location: WorkspaceViewLocation): void {
    const view = this.registry.view(id);
    if (!view || view.location !== location) {
      throw createExtensionError(
        "ERR_UNKNOWN_VIEW",
        `No ${location} view is registered as "${id}".`,
      );
    }
  }

  private withRequest(id: string, params: unknown): Record<string, ViewOpenRequest> {
    this.sequence += 1;
    return { ...this.state.openRequests, [id]: { params, sequence: this.sequence } };
  }

  /** 侧栏视图注销时显示默认侧栏。Shows the default sidebar when its view is unregistered. */
  private fallBackToDefaults(): void {
    if (!this.registry.has(this.state.activeSidebarViewId)) {
      this.update({ activeSidebarViewId: DEFAULT_SIDEBAR_VIEW_ID });
    }
  }

  private update(changes: Partial<WorkspaceLayoutState>): void {
    const next = { ...this.state, ...changes };
    if (
      next.sidebarVisible === this.state.sidebarVisible &&
      next.activeSidebarViewId === this.state.activeSidebarViewId &&
      next.fullWorkspaceViewId === this.state.fullWorkspaceViewId &&
      next.openRequests === this.state.openRequests
    ) {
      return;
    }
    this.state = next;
    for (const listener of this.listeners) listener();
  }
}
