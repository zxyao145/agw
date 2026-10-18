import type { ExtensionAction } from "@agw/extension-sdk";
import { matchesFileNamePattern } from "./file-viewer-patterns";

export type WorkspaceIcon = { kind: "lucide"; name: string } | { kind: "image"; src: string };

/** `file` 为文件查看器，在 Files 的文件视图中显示。`file` is a file viewer, shown inside the Files view. */
export type WorkspaceViewLocation = "sidebar" | "main" | "file";

export type WorkspaceViewFailure = { code: string; message: string };

/** Left Sidebar 或 Main Workspace 中的一个视图。A view of the Left Sidebar or the Main Workspace. */
export type WorkspaceView = {
  id: string;
  location: WorkspaceViewLocation;
  title: string;
  icon: WorkspaceIcon;
  extensionId: string | null;
  extensionName: string | null;
  /** extension 视图的包内入口；内置视图为 null。The extension view's package entry; null for built-ins. */
  entry: string | null;
  retainContextWhenHidden: boolean;
  /** extension 进入 failed 后保留注册并标记失败。Kept and marked when the extension fails. */
  failure: WorkspaceViewFailure | null;
  /** 文件查看器的文件名模式。A file viewer's name patterns. */
  patterns?: readonly string[];
};

/** Ribbon 项或对话扩展菜单项。A Ribbon item or a chat extension menu item. */
export type WorkspaceEntry = {
  id: string;
  extensionId: string | null;
  extensionName: string | null;
  title: string;
  icon: WorkspaceIcon;
  action: ExtensionAction;
};

export type WorkspaceEntryKind =
  | "ribbon"
  | "composerActions"
  | "messageActions"
  | "conversationActions"
  | "explorerActions";

export type WorkspaceRegistrySnapshot = {
  views: ReadonlyMap<string, WorkspaceView>;
} & Readonly<Record<WorkspaceEntryKind, readonly WorkspaceEntry[]>>;

export type ExtensionRegistration = {
  views: readonly WorkspaceView[];
  entries: Partial<Record<WorkspaceEntryKind, readonly WorkspaceEntry[]>>;
};

const ENTRY_KINDS: readonly WorkspaceEntryKind[] = [
  "ribbon",
  "composerActions",
  "messageActions",
  "conversationActions",
  "explorerActions",
];

/** 内置视图与 extension 视图、入口的注册表。The registry of built-in and extension views and entries. */
export class WorkspaceRegistry {
  private readonly builtIn: ExtensionRegistration;
  private readonly extensions = new Map<string, ExtensionRegistration>();
  private readonly listeners = new Set<() => void>();
  private snapshot: WorkspaceRegistrySnapshot;

  public constructor(builtIn: ExtensionRegistration) {
    this.builtIn = builtIn;
    this.snapshot = this.build();
  }

  public getSnapshot = (): WorkspaceRegistrySnapshot => this.snapshot;

  public subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public has(viewId: string): boolean {
    return this.snapshot.views.has(viewId);
  }

  public view(viewId: string): WorkspaceView | undefined {
    return this.snapshot.views.get(viewId);
  }

  /**
   * 文件名匹配的文件查看器：按 extension id 排序，同一 extension 内按声明顺序。
   * File viewers whose patterns match the file name, ordered by extension id and then by declaration order.
   */
  public fileViewersFor(fileName: string): WorkspaceView[] {
    return [...this.snapshot.views.values()]
      .filter(
        (view) =>
          view.location === "file" &&
          (view.patterns ?? []).some((pattern) => matchesFileNamePattern(pattern, fileName)),
      )
      .sort((left, right) =>
        left.extensionId === right.extensionId
          ? 0
          : (left.extensionId ?? "") < (right.extensionId ?? "")
            ? -1
            : 1,
      );
  }

  /** extension 进入 active 时注册它的视图与入口。Registers an extension's views and entries when it becomes active. */
  public registerExtension(extensionId: string, registration: ExtensionRegistration): void {
    if (this.extensions.has(extensionId))
      throw new Error(`Extension ${extensionId} is already registered.`);
    for (const view of registration.views) {
      if (this.snapshot.views.has(view.id))
        throw new Error(`View ${view.id} is already registered.`);
    }
    this.extensions.set(extensionId, registration);
    this.publish();
  }

  /** 停用、卸载、Reload 时注销全部视图与入口。Unregisters every view and entry on disable, uninstall or reload. */
  public unregisterExtension(extensionId: string): void {
    if (!this.extensions.delete(extensionId)) return;
    this.publish();
  }

  /** 进入 failed：注销入口，视图注册保留并标记为失败。On failure: entries go away, views stay marked as failed. */
  public markExtensionFailed(
    extensionId: string,
    failure: WorkspaceViewFailure,
    views: readonly WorkspaceView[],
  ): void {
    this.extensions.set(extensionId, {
      views: views.map((view) => ({ ...view, failure })),
      entries: {},
    });
    this.publish();
  }

  private publish(): void {
    this.snapshot = this.build();
    for (const listener of this.listeners) listener();
  }

  private build(): WorkspaceRegistrySnapshot {
    const registrations = [this.builtIn, ...this.extensions.values()];
    const views = new Map<string, WorkspaceView>();
    for (const registration of registrations) {
      for (const view of registration.views) views.set(view.id, view);
    }
    const entries = Object.fromEntries(
      ENTRY_KINDS.map((kind) => [
        kind,
        registrations.flatMap((registration) => registration.entries[kind] ?? []),
      ]),
    ) as Record<WorkspaceEntryKind, WorkspaceEntry[]>;
    return { views, ...entries };
  }
}
