import { createExtensionError } from "@agw/extension-sdk";
import { DEFAULT_MAIN_VIEW_ID, DEFAULT_SIDEBAR_VIEW_ID } from "./workspace-ids";
import type { WorkspaceRegistry, WorkspaceViewLocation } from "./workspace-registry";

export type ViewOpenRequest = { params: unknown; sequence: number };

export type WorkspaceLayoutState = {
  sidebarVisible: boolean;
  activeSidebarViewId: string;
  activeMainViewId: string;
  /** 每个视图最近一次打开时的参数与序号。The latest open params and sequence per view. */
  openRequests: Readonly<Record<string, ViewOpenRequest>>;
};

export const DEFAULT_WORKSPACE_LAYOUT: WorkspaceLayoutState = {
  sidebarVisible: true,
  activeSidebarViewId: DEFAULT_SIDEBAR_VIEW_ID,
  activeMainViewId: DEFAULT_MAIN_VIEW_ID,
  openRequests: {},
};

/** 三区布局状态，保存在内存中，进出 Settings 不丢失。Three-region layout state kept in memory across Settings. */
export class WorkspaceLayoutStore {
  private state: WorkspaceLayoutState = DEFAULT_WORKSPACE_LAYOUT;
  private sequence = 0;
  private navigateToChat: (() => void) | null = null;
  private readonly listeners = new Set<() => void>();

  public constructor(private readonly registry: WorkspaceRegistry) {
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
      sidebarVisible: true,
      activeSidebarViewId: id,
      openRequests: this.withRequest(id, {}),
    });
  }

  public setSidebarVisible(visible: boolean): void {
    this.update({ sidebarVisible: visible });
  }

  public openMainView(id: string, params: unknown = {}): void {
    this.assertView(id, "main");
    this.navigateToChat?.();
    this.update({ activeMainViewId: id, openRequests: this.withRequest(id, params) });
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

  /** 当前视图被注销时回到默认内置视图。Falls back to the default built-in view when the current one is unregistered. */
  private fallBackToDefaults(): void {
    const changes: Partial<WorkspaceLayoutState> = {};
    if (!this.registry.has(this.state.activeSidebarViewId))
      changes.activeSidebarViewId = DEFAULT_SIDEBAR_VIEW_ID;
    if (!this.registry.has(this.state.activeMainViewId))
      changes.activeMainViewId = DEFAULT_MAIN_VIEW_ID;
    if (Object.keys(changes).length > 0) this.update(changes);
  }

  private update(changes: Partial<WorkspaceLayoutState>): void {
    const next = { ...this.state, ...changes };
    if (
      next.sidebarVisible === this.state.sidebarVisible &&
      next.activeSidebarViewId === this.state.activeSidebarViewId &&
      next.activeMainViewId === this.state.activeMainViewId &&
      next.openRequests === this.state.openRequests
    ) {
      return;
    }
    this.state = next;
    for (const listener of this.listeners) listener();
  }
}
