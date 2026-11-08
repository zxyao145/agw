import { createHash } from "node:crypto";
import { readdir, readFile, realpath, stat } from "node:fs/promises";
import { basename, extname, join, relative, isAbsolute, sep } from "node:path";

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
import { type ThemeStyleCompiler, themeStyleCompiler } from "./extension-theme-style-cache";

export const MAX_ICON_BYTES = 256 * 1024;

/** 包内图标扩展名对应的 MIME。The MIME type of each package icon extension. */
const ICON_MIME_TYPES: Readonly<Record<string, string>> = {
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".jpeg": "image/jpeg",
  ".webp": "image/webp",
};

/** 扫描得到的一个 extension 目录。One scanned extension directory. */
export type CatalogEntry = {
  id: string;
  instanceId?: string;
  origin?: InstalledExtension["origin"];
  releaseHash?: string;
  serverAvailable?: boolean;
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
 * 读取并校验一个 extension 根目录；`expectedId` 为 null 时（暂存目录）不检查目录名。CSS 主题由 `compiler` 编译与缓存。
 * Reads and validates one extension root directory; a null `expectedId` (staging) skips the directory-name check.
 * `compiler` compiles and caches CSS themes.
 */
export async function readExtensionDirectory(
  directory: string,
  appVersion: string,
  expectedId: string | null = basename(directory),
  compiler: ThemeStyleCompiler = themeStyleCompiler,
  options: { serverProjection?: boolean } = {},
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
  const serverProjection =
    options.serverProjection === true && ((manifest?.targets ?? 0) & 1) !== 0;
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
    for (const [path, expectedChecksum] of Object.entries(manifest.files ?? {})) {
      if (serverProjection && !path.startsWith("desktop/")) continue;
      const field = `files.${path}`;
      const file = join(rootRealPath, ...path.split("/"));
      const fileRealPath = await realPathOrNull(file);
      if (fileRealPath === null) {
        errors.push({
          code: "PACKAGE_CHECKSUM_FILE_NOT_FOUND",
          message: `The checksummed file "${path}" does not exist.`,
          path: field,
        });
        continue;
      }
      if (!isPathInside(rootRealPath, fileRealPath)) {
        errors.push({
          code: "PACKAGE_PATH_OUTSIDE_ROOT",
          message: `The checksummed file "${path}" resolves outside the extension directory.`,
          path: field,
        });
        continue;
      }
      if (!(await stat(fileRealPath)).isFile()) {
        errors.push({
          code: "PACKAGE_CHECKSUM_FILE_NOT_FOUND",
          message: `The checksummed path "${path}" is not a file.`,
          path: field,
        });
        continue;
      }
      const actualChecksum = createHash("sha256")
        .update(await readFile(fileRealPath))
        .digest("hex");
      if (actualChecksum !== expectedChecksum) {
        errors.push({
          code: "PACKAGE_CHECKSUM_MISMATCH",
          message: `The SHA-256 of "${path}" does not match its manifest checksum.`,
          path: field,
        });
      }
    }
    if (manifest.manifestVersion === 2) {
      const packageFiles = await readdir(rootRealPath, { recursive: true, withFileTypes: true });
      const checksummedPaths = new Set(Object.keys(manifest.files ?? {}));
      for (const item of packageFiles) {
        const filePath = join(item.parentPath, item.name);
        const path = relative(rootRealPath, filePath).split(sep).join("/");
        if (path === "manifest.json" || item.isDirectory()) continue;
        if (serverProjection && !path.startsWith("desktop/")) {
          errors.push({
            code: "SERVER_PACKAGE_FILE_NOT_ALLOWED",
            message: `The Server package includes a file outside the Desktop directory: "${path}".`,
            path: `files.${path}`,
          });
          continue;
        }
        if (item.isSymbolicLink()) {
          errors.push({
            code: "SYMLINK_NOT_ALLOWED",
            message: `Symbolic links are not allowed: ${path}.`,
            path: `files.${path}`,
          });
          continue;
        }
        if (item.isFile() && !checksummedPaths.has(path)) {
          errors.push({
            code: "PACKAGE_CHECKSUM_REQUIRED",
            message: `The package file "${path}" has no manifest checksum.`,
            path: `files.${path}`,
          });
        }
      }
    }
    for (const reference of manifestPackagePaths(manifest)) {
      if (serverProjection && !reference.path.startsWith("desktop/")) {
        errors.push({
          code: "DESKTOP_RESOURCE_PATH_INVALID",
          message: `Desktop resource "${reference.path}" must be under the desktop directory.`,
          path: reference.field,
        });
        continue;
      }
      if (
        serverProjection &&
        manifest.manifestVersion === 2 &&
        !Object.hasOwn(manifest.files ?? {}, reference.path)
      ) {
        errors.push({
          code: "PACKAGE_CHECKSUM_REQUIRED",
          message: `The Desktop resource "${reference.path}" has no manifest checksum.`,
          path: `files.${reference.path}`,
        });
      }
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
      const mimeType = ICON_MIME_TYPES[extname(reference.path)];
      icons[reference.path] =
        `data:${mimeType};base64,${(await readFile(fileRealPath)).toString("base64")}`;
    }
    // 主题文件在包内路径检查之后读取与校验。Theme files are read and validated after the package path checks.
    const result = await readExtensionThemes(
      manifest.contributes?.themes ?? [],
      themeFiles,
      rootRealPath,
      compiler,
    );
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
    entries.push({ ...entry, id: name, instanceId: name, source });
  }
  return entries;
}

/**
 * 合并内置、用户与 Server 目录：用户副本优先于内置副本，同 manifest 的活动 Server 副本优先。结果按 instanceId 排序。
 * Merges bundled, user and Server directories: user copies win over bundled copies, active Server copies win over
 * local copies with the same id, and the result is sorted by instanceId.
 */
export function mergeExtensionCatalogs(
  bundled: readonly SourcedCatalogEntry[],
  user: readonly SourcedCatalogEntry[],
  removedBundled: readonly string[],
  server: readonly SourcedCatalogEntry[] = [],
): SourcedCatalogEntry[] {
  const merged = new Map<string, SourcedCatalogEntry>();
  for (const entry of bundled) {
    if (!removedBundled.includes(entry.id)) merged.set(entry.instanceId ?? entry.id, entry);
  }
  for (const entry of user) merged.set(entry.instanceId ?? entry.id, entry);
  for (const entry of server) {
    if (entry.serverAvailable !== false && entry.validation === "valid" && entry.manifest) {
      for (const [key, local] of merged) {
        if (local.source !== "server" && local.id === entry.id) merged.delete(key);
      }
    }
    merged.set(entry.instanceId ?? entry.id, entry);
  }
  return [...merged.values()].sort((left, right) => {
    const leftId = left.instanceId ?? left.id;
    const rightId = right.instanceId ?? right.id;
    return leftId < rightId ? -1 : leftId > rightId ? 1 : 0;
  });
}

export function toInstalledExtension(
  entry: SourcedCatalogEntry,
  settings: ExtensionSettings | undefined,
): InstalledExtension {
  return {
    id: entry.id,
    instanceId: entry.instanceId ?? entry.id,
    source: entry.source,
    origin: entry.origin,
    releaseHash: entry.releaseHash,
    serverAvailable: entry.source === "server" ? (entry.serverAvailable ?? true) : undefined,
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
