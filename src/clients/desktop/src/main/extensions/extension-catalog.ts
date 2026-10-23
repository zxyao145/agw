import { readdir, readFile, realpath, stat } from "node:fs/promises";
import { basename, join, relative, isAbsolute, sep } from "node:path";

import type {
  ExtensionGrants,
  ExtensionSettings,
  ExtensionSource,
  ExtensionTheme,
  ExtensionValidation,
  ExtensionValidationError,
  InstalledExtension,
} from "../../shared/contracts";
import type { ExtensionManifest } from "@agw/extension-sdk";
import { evaluateExtensionManifest, manifestPackagePaths } from "../../shared/extension-manifest";
import { isGrantCovered } from "../settings/settings-store";
import { readExtensionThemes } from "./extension-theme-file";

export const MAX_ICON_BYTES = 256 * 1024;

/** 扫描得到的一个 extension 目录。One scanned extension directory. */
export type CatalogEntry = {
  id: string;
  directory: string;
  manifest: ExtensionManifest | null;
  validation: ExtensionValidation;
  errors: ExtensionValidationError[];
  icons: Record<string, string>;
  themes: ExtensionTheme[];
};

export function isPathInside(root: string, target: string): boolean {
  const path = relative(root, target);
  return path === "" || (!path.startsWith(`..${sep}`) && path !== ".." && !isAbsolute(path));
}

export function declaredGrants(manifest: ExtensionManifest): ExtensionGrants {
  return {
    permissions: [...(manifest.permissions ?? [])],
    hostPermissions: [...(manifest.hostPermissions ?? [])],
  };
}

async function realPathOrNull(path: string): Promise<string | null> {
  try {
    return await realpath(path);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return null;
    throw error;
  }
}

function errorEntry(directory: string, errors: ExtensionValidationError[]): CatalogEntry {
  return {
    id: basename(directory),
    directory,
    manifest: null,
    validation: "invalid",
    errors,
    icons: {},
    themes: [],
  };
}

/**
 * 读取并校验一个 extension 根目录；`expectedId` 为 null 时（暂存目录）不检查目录名。
 * Reads and validates one extension root directory; a null `expectedId` (staging) skips the directory-name check.
 */
export async function readExtensionDirectory(
  directory: string,
  appVersion: string,
  expectedId: string | null = basename(directory),
): Promise<CatalogEntry> {
  const rootRealPath = await realpath(directory);
  const manifestFile = join(directory, "manifest.json");
  const manifestRealPath = await realPathOrNull(manifestFile);
  if (manifestRealPath === null) {
    return errorEntry(directory, [
      { code: "MANIFEST_NOT_FOUND", message: "manifest.json is missing.", path: null },
    ]);
  }
  if (!isPathInside(rootRealPath, manifestRealPath)) {
    return errorEntry(directory, [
      {
        code: "PACKAGE_PATH_OUTSIDE_ROOT",
        message: "manifest.json resolves outside the extension directory.",
        path: null,
      },
    ]);
  }

  let raw: unknown;
  try {
    raw = JSON.parse(await readFile(manifestFile, "utf8"));
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error;
    return errorEntry(directory, [
      {
        code: "MANIFEST_PARSE_FAILED",
        message: `manifest.json is not valid JSON: ${error.message}`,
        path: null,
      },
    ]);
  }

  const evaluation = evaluateExtensionManifest(raw, appVersion);
  const manifest = evaluation.manifest;
  const errors = [...evaluation.errors];
  const icons: Record<string, string> = {};
  let themes: ExtensionTheme[] = [];
  if (manifest) {
    const themeFiles = new Map<string, string>();
    if (expectedId !== null && manifest.id !== expectedId) {
      errors.push({
        code: "DIRECTORY_NAME_MISMATCH",
        message: `The directory name must equal the extension id "${manifest.id}".`,
        path: "id",
      });
    }
    for (const reference of manifestPackagePaths(manifest)) {
      const file = join(rootRealPath, ...reference.path.split("/"));
      const fileRealPath = await realPathOrNull(file);
      if (fileRealPath === null) {
        errors.push({
          code: "PACKAGE_FILE_NOT_FOUND",
          message: `"${reference.path}" does not exist.`,
          path: reference.field,
        });
        continue;
      }
      if (!isPathInside(rootRealPath, fileRealPath)) {
        errors.push({
          code: "PACKAGE_PATH_OUTSIDE_ROOT",
          message: `"${reference.path}" resolves outside the extension directory.`,
          path: reference.field,
        });
        continue;
      }
      const info = await stat(fileRealPath);
      if (!info.isFile()) {
        errors.push({
          code: "PACKAGE_FILE_NOT_FOUND",
          message: `"${reference.path}" is not a file.`,
          path: reference.field,
        });
        continue;
      }
      if (reference.kind === "theme") themeFiles.set(reference.path, fileRealPath);
      if (reference.kind !== "icon" || reference.path in icons) continue;
      if (info.size > MAX_ICON_BYTES) {
        errors.push({
          code: "ICON_TOO_LARGE",
          message: `"${reference.path}" exceeds ${MAX_ICON_BYTES / 1024} KB.`,
          path: reference.field,
        });
        continue;
      }
      const mimeType = reference.path.endsWith(".svg") ? "image/svg+xml" : "image/png";
      icons[reference.path] =
        `data:${mimeType};base64,${(await readFile(fileRealPath)).toString("base64")}`;
    }
    // 主题文件在包内路径检查之后读取与校验。Theme files are read and validated after the package path checks.
    const result = await readExtensionThemes(manifest.contributes?.themes ?? [], themeFiles);
    themes = result.themes;
    errors.push(...result.errors);
  }

  const validation: ExtensionValidation =
    errors.length > evaluation.errors.length ? "invalid" : evaluation.validation;
  return {
    id: manifest?.id ?? basename(directory),
    directory,
    manifest,
    validation,
    errors,
    icons: validation === "valid" ? icons : {},
    themes: validation === "valid" ? themes : [],
  };
}

/** 带来源的目录项。A catalog entry with its source. */
export type SourcedCatalogEntry = CatalogEntry & { source: ExtensionSource };

/** 扫描安装目录，跳过以 `.` 开头的目录（包括 `.staging`）。Scans the install directory, skipping dot directories such as `.staging`. */
export async function scanExtensionDirectory(
  extensionsDirectory: string,
  appVersion: string,
  source: ExtensionSource,
): Promise<SourcedCatalogEntry[]> {
  let names: string[];
  try {
    names = await readdir(extensionsDirectory);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return [];
    throw error;
  }
  const entries: SourcedCatalogEntry[] = [];
  for (const name of names.sort()) {
    if (name.startsWith(".")) continue;
    const directory = join(extensionsDirectory, name);
    if (!(await stat(directory)).isDirectory()) continue;
    const entry = await readExtensionDirectory(directory, appVersion);
    entries.push({ ...entry, id: name, source });
  }
  return entries;
}

/**
 * 合并内置与用户目录：同 id 时用户 extension 优先；用户卸载过的内置 extension 不出现。结果按 id 排序。
 * Merges the bundled and user directories: user extensions win on the same id, uninstalled bundled extensions are
 * left out, and the result is sorted by id.
 */
export function mergeExtensionCatalogs(
  bundled: readonly SourcedCatalogEntry[],
  user: readonly SourcedCatalogEntry[],
  removedBundled: readonly string[],
): SourcedCatalogEntry[] {
  const merged = new Map<string, SourcedCatalogEntry>();
  for (const entry of bundled) {
    if (!removedBundled.includes(entry.id)) merged.set(entry.id, entry);
  }
  for (const entry of user) merged.set(entry.id, entry);
  return [...merged.values()].sort((left, right) => (left.id < right.id ? -1 : 1));
}

export function toInstalledExtension(
  entry: SourcedCatalogEntry,
  settings: ExtensionSettings | undefined,
): InstalledExtension {
  return {
    id: entry.id,
    source: entry.source,
    directory: entry.directory,
    manifest: entry.manifest,
    validation: entry.validation,
    errors: entry.errors,
    enabled: settings?.enabled ?? false,
    permissionApprovalRequired:
      settings !== undefined &&
      entry.manifest !== null &&
      !isGrantCovered(declaredGrants(entry.manifest), settings),
    grantedPermissions: settings?.grantedPermissions ?? [],
    grantedHostPermissions: settings?.grantedHostPermissions ?? [],
    lastActivatedVersion: settings?.lastActivatedVersion ?? null,
    icons: entry.icons,
    themes: entry.themes,
  };
}
