import type { ExtensionContributes, ExtensionManifest } from "@agw/extension-sdk";
import type { ExtensionState } from "./extension-host";

/** manifest 声明的主题名称，停用或不合法的 extension 同样有。Theme titles from the manifest, also for disabled or invalid extensions. */
export function themeTitles(manifest: ExtensionManifest | null): string[] {
  return manifest?.contributes?.themes?.map((theme) => theme.title) ?? [];
}

function contributes(extension: ExtensionState): ExtensionContributes {
  return extension.installed.manifest?.contributes ?? {};
}

function hasAny(...lists: (readonly unknown[] | undefined)[]): boolean {
  return lists.some((list) => (list?.length ?? 0) > 0);
}

/**
 * Extensions 页面按 manifest 的 contributes 划分的类型，键是 `category` 参数的取值。一个 extension 可以属于多个类型。
 * The Extensions page types, derived from the manifest's contributes and keyed by the `category` parameter. An
 * extension can belong to several types.
 */
export const EXTENSION_CATEGORIES: Readonly<
  Record<string, { label: string; matches(extension: ExtensionState): boolean }>
> = {
  themes: {
    label: "Theme",
    matches: (extension) => themeTitles(extension.installed.manifest).length > 0,
  },
  "file-viewers": {
    label: "File viewer",
    matches: (extension) => hasAny(contributes(extension).fileViewers),
  },
  views: {
    label: "View",
    matches: (extension) => {
      const { sidebarViews, mainViews } = contributes(extension);
      return hasAny(sidebarViews, mainViews);
    },
  },
  commands: {
    label: "Command",
    matches: (extension) => {
      const { commands, ribbon, chat, explorerActions } = contributes(extension);
      return hasAny(
        commands,
        ribbon,
        chat?.composerActions,
        chat?.messageActions,
        chat?.conversationActions,
        explorerActions,
      );
    },
  },
};

/** extension 所属的类型，按 `EXTENSION_CATEGORIES` 的顺序。The types of an extension, in `EXTENSION_CATEGORIES` order. */
export function extensionCategories(extension: ExtensionState): string[] {
  return Object.keys(EXTENSION_CATEGORIES).filter((category) =>
    EXTENSION_CATEGORIES[category].matches(extension),
  );
}

/**
 * 按 `category` 筛选；没有参数时返回全部，未知分类返回 null。
 * Filters by `category`; no parameter returns everything and an unknown category returns null.
 */
export function extensionsInCategory(
  extensions: readonly ExtensionState[],
  category: string | null,
): readonly ExtensionState[] | null {
  if (category === null) return extensions;
  const filter = Object.hasOwn(EXTENSION_CATEGORIES, category)
    ? EXTENSION_CATEGORIES[category]
    : null;
  return filter ? extensions.filter((extension) => filter.matches(extension)) : null;
}
