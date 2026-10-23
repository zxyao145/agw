import type { ExtensionManifest } from "@agw/extension-sdk";
import type { ExtensionState } from "./extension-host";

/** manifest 声明的主题名称，停用或不合法的 extension 同样有。Theme titles from the manifest, also for disabled or invalid extensions. */
export function themeTitles(manifest: ExtensionManifest | null): string[] {
  return manifest?.contributes?.themes?.map((theme) => theme.title) ?? [];
}

/** Extensions 页面 `category` 参数的筛选。The filters of the Extensions page `category` parameter. */
export const EXTENSION_CATEGORIES: Readonly<
  Record<string, { label: string; matches(extension: ExtensionState): boolean }>
> = {
  themes: {
    label: "Theme extensions",
    matches: (extension) => themeTitles(extension.installed.manifest).length > 0,
  },
};

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
