import type { InstalledExtension } from "@desktop/shared/contracts";
import type { ExtensionActionContribution, ExtensionManifest } from "@agw/extension-sdk";
import { extensionViewId } from "./workspace-ids";
import type {
  ExtensionRegistration,
  WorkspaceEntry,
  WorkspaceEntryKind,
  WorkspaceIcon,
  WorkspaceView,
} from "./workspace-registry";

/** lucide-react 导出名或包内图标（主进程转换后的 data URL）。A lucide-react name or a package icon (data URL). */
export function resolveIcon(
  icon: string | undefined,
  icons: Readonly<Record<string, string>>,
): WorkspaceIcon {
  if (icon === undefined) return { kind: "lucide", name: "Puzzle" };
  const dataUrl = icons[icon];
  return dataUrl ? { kind: "image", src: dataUrl } : { kind: "lucide", name: icon };
}

export function extensionIcon(installed: InstalledExtension): WorkspaceIcon {
  return resolveIcon(installed.manifest?.icon, installed.icons);
}

/** extension 声明的全部视图。Every view an extension declares. */
export function extensionViews(installed: InstalledExtension): WorkspaceView[] {
  const manifest = installed.manifest;
  if (!manifest) return [];
  const base = {
    extensionId: installed.id,
    extensionName: manifest.name,
    failure: null,
  };
  return [
    ...(manifest.contributes?.sidebarViews ?? []).map((view): WorkspaceView => ({
      ...base,
      id: extensionViewId(installed.id, view.id),
      location: "sidebar",
      title: view.title,
      icon: resolveIcon(view.icon, installed.icons),
      entry: view.entry,
      retainContextWhenHidden: false,
      priority: "default",
    })),
    ...(manifest.contributes?.mainViews ?? []).map((view): WorkspaceView => ({
      ...base,
      id: extensionViewId(installed.id, view.id),
      location: "main",
      title: view.title,
      icon: resolveIcon(view.icon, installed.icons),
      entry: view.entry,
      retainContextWhenHidden: view.retainContextWhenHidden ?? false,
      priority: "default",
    })),
    ...(manifest.contributes?.fileViewers ?? []).map((viewer): WorkspaceView => ({
      ...base,
      id: extensionViewId(installed.id, viewer.id),
      location: "file",
      title: viewer.title,
      icon: resolveIcon(viewer.icon, installed.icons),
      entry: viewer.entry,
      // 文件查看器隐藏时保留页面，由文件变化决定关闭。File viewers keep their page when hidden; file changes close them.
      retainContextWhenHidden: true,
      patterns: viewer.patterns,
      priority: viewer.priority ?? "default",
    })),
  ];
}

function entries(
  installed: InstalledExtension,
  manifest: ExtensionManifest,
  items: readonly ExtensionActionContribution[] | undefined,
): WorkspaceEntry[] {
  return (items ?? []).map((item) => ({
    id: `${installed.id}:${item.id}`,
    extensionId: installed.id,
    extensionName: manifest.name,
    title: item.title,
    icon: resolveIcon(item.icon, installed.icons),
    action:
      item.action.type === "command"
        ? item.action
        : { ...item.action, view: extensionViewId(installed.id, item.action.view) },
  }));
}

/** extension 进入 active 时注册的视图与入口。Views and entries registered when an extension becomes active. */
export function extensionRegistration(installed: InstalledExtension): ExtensionRegistration {
  const manifest = installed.manifest;
  if (!manifest) return { views: [], entries: {} };
  const contributes = manifest.contributes ?? {};
  const byKind: Record<WorkspaceEntryKind, readonly ExtensionActionContribution[] | undefined> = {
    ribbon: contributes.ribbon,
    composerActions: contributes.chat?.composerActions,
    messageActions: contributes.chat?.messageActions,
    conversationActions: contributes.chat?.conversationActions,
    explorerActions: contributes.explorerActions,
  };
  return {
    views: extensionViews(installed),
    entries: Object.fromEntries(
      Object.entries(byKind).map(([kind, items]) => [kind, entries(installed, manifest, items)]),
    ),
  };
}
