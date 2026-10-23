import { randomUUID } from "node:crypto";
import { createWriteStream } from "node:fs";
import { cp, lstat, mkdir, readdir, readFile, rename, rm, stat } from "node:fs/promises";
import { dirname, join } from "node:path";
import { Transform } from "node:stream";
import { pipeline } from "node:stream/promises";
import { open as openZip, type Entry, type ZipFile } from "yauzl";

import type {
  ExtensionGrants,
  ExtensionRecoveryNotice,
  ExtensionValidationError,
} from "../../shared/contracts";
import type { ExtensionManifest } from "@agw/extension-sdk";
import { writeJsonFile } from "../settings/json-file";
import { type CatalogEntry, declaredGrants, readExtensionDirectory } from "./extension-catalog";

export const MAX_ARCHIVE_ENTRIES = 10_000;
export const MAX_ARCHIVE_BYTES = 500 * 1024 * 1024;

const SYMLINK_MODE = 0o120000;
const FILE_TYPE_MASK = 0o170000;

/** 安装、卸载过程中可以报告给用户的错误。An install or uninstall error reported to the user. */
export class ExtensionInstallError extends Error {
  public constructor(
    public readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = "ExtensionInstallError";
  }
}

export type PreparedExtensionInstall = {
  installId: string;
  manifest: ExtensionManifest | null;
  errors: ExtensionValidationError[];
  installedVersion: string | null;
};

type InstallJournal =
  | {
      operation: "install";
      extensionId: string;
      phase: "swapping" | "committed";
      hasPrevious: boolean;
    }
  | { operation: "uninstall"; extensionId: string; deleteData: boolean; phase: "removing" };

export type ExtensionInstallerOptions = {
  /** `<userData>/extensions/packages`，暂存目录 `.staging` 也在这里。Also holds the `.staging` directory. */
  packagesDirectory: string;
  /** `<userData>/extensions/data`。 */
  dataDirectory: string;
  appVersion: string;
  settings: {
    applyInstalledExtension(extensionId: string, declared: ExtensionGrants): Promise<void>;
    removeExtension(extensionId: string): Promise<void>;
    removeBundledExtension(extensionId: string): Promise<void>;
  };
  /** 清除该 extension 的 session 数据。Clears the extension's session data. */
  clearSessionData(extensionId: string): Promise<void>;
  log?(message: string): void;
  /** 压缩包上限，默认 10,000 个条目、500 MB。Archive limits; defaults are 10,000 entries and 500 MB. */
  archiveLimits?: ArchiveLimits;
};

export type ArchiveLimits = {
  maxEntries: number;
  maxBytes: number;
};

async function exists(path: string): Promise<boolean> {
  try {
    await lstat(path);
    return true;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return false;
    throw error;
  }
}

function installError(error: unknown): ExtensionValidationError {
  if (error instanceof ExtensionInstallError) {
    return { code: error.code, message: error.message, path: null };
  }
  throw error;
}

/** 管理暂存目录、提交、卸载与启动恢复。Manages staging, commits, uninstalls and startup recovery. */
export class ExtensionInstaller {
  public readonly stagingDirectory: string;
  private pending: Promise<unknown> = Promise.resolve();

  public constructor(private readonly options: ExtensionInstallerOptions) {
    this.stagingDirectory = join(options.packagesDirectory, ".staging");
  }

  public prepareFromDirectory(source: string): Promise<PreparedExtensionInstall> {
    return this.serialize(async () => {
      const installId = randomUUID();
      const packageDirectory = this.packageDirectory(installId);
      try {
        if (!(await exists(join(source, "manifest.json")))) {
          throw new ExtensionInstallError(
            "MANIFEST_NOT_AT_ROOT",
            "manifest.json must be directly inside the selected folder.",
          );
        }
        await mkdir(dirname(packageDirectory), { recursive: true });
        await cp(source, packageDirectory, {
          recursive: true,
          errorOnExist: true,
          force: false,
          filter: async (path) => {
            if ((await lstat(path)).isSymbolicLink()) {
              throw new ExtensionInstallError(
                "SYMLINK_NOT_ALLOWED",
                `Symbolic links are not allowed: ${path}`,
              );
            }
            return true;
          },
        });
      } catch (error) {
        await rm(this.installDirectory(installId), { recursive: true, force: true });
        return { installId, manifest: null, errors: [installError(error)], installedVersion: null };
      }
      return this.preview(installId);
    });
  }

  public prepareFromArchive(file: string): Promise<PreparedExtensionInstall> {
    return this.serialize(async () => {
      const installId = randomUUID();
      const extracted = join(this.installDirectory(installId), "extracted");
      try {
        await mkdir(extracted, { recursive: true });
        await extractArchive(
          file,
          extracted,
          this.options.archiveLimits ?? {
            maxEntries: MAX_ARCHIVE_ENTRIES,
            maxBytes: MAX_ARCHIVE_BYTES,
          },
        );
        const root = await findArchiveRoot(extracted);
        await rename(root, this.packageDirectory(installId));
        await rm(extracted, { recursive: true, force: true });
      } catch (error) {
        await rm(this.installDirectory(installId), { recursive: true, force: true });
        return { installId, manifest: null, errors: [installError(error)], installedVersion: null };
      }
      return this.preview(installId);
    });
  }

  public commit(installId: string): Promise<CatalogEntry> {
    return this.serialize(async () => {
      const staged = await readExtensionDirectory(
        this.packageDirectory(installId),
        this.options.appVersion,
        null,
      );
      if (staged.errors.length > 0 || !staged.manifest) {
        throw new Error("An install with errors cannot be committed.");
      }
      const extensionId = staged.manifest.id;
      const target = join(this.options.packagesDirectory, extensionId);
      const hasPrevious = await exists(target);
      const journalFile = this.journalFile(installId);

      await writeJsonFile(journalFile, {
        operation: "install",
        extensionId,
        phase: "swapping",
        hasPrevious,
      } satisfies InstallJournal);
      if (hasPrevious) await rename(target, join(this.installDirectory(installId), "previous"));
      await rename(this.packageDirectory(installId), target);
      await this.options.settings.applyInstalledExtension(
        extensionId,
        declaredGrants(staged.manifest),
      );
      await writeJsonFile(journalFile, {
        operation: "install",
        extensionId,
        phase: "committed",
        hasPrevious,
      } satisfies InstallJournal);
      await rm(this.installDirectory(installId), { recursive: true, force: true });
      return readExtensionDirectory(target, this.options.appVersion);
    });
  }

  public cancel(installId: string): Promise<void> {
    return this.serialize(() =>
      rm(this.installDirectory(installId), { recursive: true, force: true }),
    );
  }

  public uninstall(extensionId: string, deleteData: boolean): Promise<void> {
    return this.serialize(async () => {
      const target = join(this.options.packagesDirectory, extensionId);
      if (!(await exists(target))) throw new Error(`Extension ${extensionId} is not installed.`);
      const uninstallId = randomUUID();
      await writeJsonFile(this.journalFile(uninstallId), {
        operation: "uninstall",
        extensionId,
        deleteData,
        phase: "removing",
      } satisfies InstallJournal);
      await rename(target, join(this.installDirectory(uninstallId), "previous"));
      await this.finishUninstall(uninstallId, extensionId, deleteData);
    });
  }

  /**
   * 卸载内置 extension：应用资源目录只读，记录卸载并按需删除数据。
   * Uninstalls a bundled extension: the app resources are read-only, so record the removal and delete data on request.
   */
  public removeBundled(extensionId: string, deleteData: boolean): Promise<void> {
    return this.serialize(async () => {
      await this.options.settings.removeBundledExtension(extensionId);
      if (deleteData) await this.deleteData(extensionId);
    });
  }

  /** 启动时、扫描之前恢复未完成的替换与卸载。Recovers unfinished replacements and uninstalls at startup, before scanning. */
  public recover(): Promise<ExtensionRecoveryNotice[]> {
    return this.serialize(async () => {
      let names: string[];
      try {
        names = await readdir(this.stagingDirectory);
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code === "ENOENT") return [];
        throw error;
      }
      const notices: ExtensionRecoveryNotice[] = [];
      for (const name of names) {
        const notice = await this.recoverOne(name);
        if (notice) notices.push(notice);
      }
      return notices;
    });
  }

  private async recoverOne(installId: string): Promise<ExtensionRecoveryNotice | null> {
    const directory = this.installDirectory(installId);
    let journal: InstallJournal;
    try {
      journal = JSON.parse(await readFile(this.journalFile(installId), "utf8")) as InstallJournal;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
      await rm(directory, { recursive: true, force: true });
      return null;
    }

    const target = join(this.options.packagesDirectory, journal.extensionId);
    const previous = join(directory, "previous");
    if (journal.operation === "install") {
      if (journal.phase === "committed") {
        await rm(directory, { recursive: true, force: true });
        return null;
      }
      if (await exists(this.packageDirectory(installId))) {
        const restored = await exists(previous);
        if (restored) await rename(previous, target);
        await rm(directory, { recursive: true, force: true });
        this.options.log?.(`Extension ${journal.extensionId}: unfinished install rolled back.`);
        return restored
          ? { extensionId: journal.extensionId, operation: "install", outcome: "restored-previous" }
          : null;
      }
      const installed = await readExtensionDirectory(target, this.options.appVersion);
      if (installed.manifest) {
        await this.options.settings.applyInstalledExtension(
          journal.extensionId,
          declaredGrants(installed.manifest),
        );
      }
      await writeJsonFile(this.journalFile(installId), { ...journal, phase: "committed" });
      await rm(directory, { recursive: true, force: true });
      this.options.log?.(`Extension ${journal.extensionId}: unfinished install completed.`);
      return { extensionId: journal.extensionId, operation: "install", outcome: "completed" };
    }

    if (await exists(target)) {
      await rm(directory, { recursive: true, force: true });
      this.options.log?.(`Extension ${journal.extensionId}: unfinished uninstall discarded.`);
      return null;
    }
    await this.finishUninstall(installId, journal.extensionId, journal.deleteData);
    this.options.log?.(`Extension ${journal.extensionId}: unfinished uninstall completed.`);
    return { extensionId: journal.extensionId, operation: "uninstall", outcome: "completed" };
  }

  private async finishUninstall(
    uninstallId: string,
    extensionId: string,
    deleteData: boolean,
  ): Promise<void> {
    await this.options.settings.removeExtension(extensionId);
    if (deleteData) await this.deleteData(extensionId);
    await rm(this.installDirectory(uninstallId), { recursive: true, force: true });
  }

  /** 删除存储目录与 session 数据。Deletes the storage directory and session data. */
  private async deleteData(extensionId: string): Promise<void> {
    await rm(join(this.options.dataDirectory, extensionId), { recursive: true, force: true });
    await this.options.clearSessionData(extensionId);
  }

  private async preview(installId: string): Promise<PreparedExtensionInstall> {
    const staged = await readExtensionDirectory(
      this.packageDirectory(installId),
      this.options.appVersion,
      null,
    );
    let installedVersion: string | null = null;
    if (staged.manifest) {
      const installed = join(this.options.packagesDirectory, staged.manifest.id);
      if (await exists(installed)) {
        installedVersion =
          (await readExtensionDirectory(installed, this.options.appVersion)).manifest?.version ??
          null;
      }
    }
    return { installId, manifest: staged.manifest, errors: staged.errors, installedVersion };
  }

  private installDirectory(installId: string): string {
    if (!/^[0-9a-f-]{36}$/u.test(installId)) throw new Error("Invalid install id.");
    return join(this.stagingDirectory, installId);
  }

  private packageDirectory(installId: string): string {
    return join(this.installDirectory(installId), "package");
  }

  private journalFile(installId: string): string {
    return join(this.installDirectory(installId), "journal.json");
  }

  private serialize<T>(operation: () => Promise<T>): Promise<T> {
    const result = this.pending.then(operation);
    this.pending = result.catch(() => undefined);
    return result;
  }
}

function openArchive(file: string): Promise<ZipFile> {
  return new Promise((resolve, reject) =>
    openZip(
      file,
      { lazyEntries: true, autoClose: true, strictFileNames: true, validateEntrySizes: true },
      (error, zipfile) => (error ? reject(error) : resolve(zipfile)),
    ),
  );
}

function openEntryStream(zipfile: ZipFile, entry: Entry): Promise<NodeJS.ReadableStream> {
  return new Promise((resolve, reject) =>
    zipfile.openReadStream(entry, (error, stream) => (error ? reject(error) : resolve(stream))),
  );
}

function archiveError(error: Error): ExtensionInstallError {
  if (
    /^(absolute path|invalid relative path|invalid characters in fileName)/u.test(error.message)
  ) {
    return new ExtensionInstallError("ARCHIVE_PATH_NOT_ALLOWED", error.message);
  }
  return new ExtensionInstallError("ARCHIVE_INVALID", error.message);
}

/** 逐项解压，按实际写入的字节计数。Extracts entry by entry, counting the bytes actually written. */
async function extractArchive(
  file: string,
  destination: string,
  limits: ArchiveLimits,
): Promise<void> {
  let zipfile: ZipFile;
  try {
    zipfile = await openArchive(file);
  } catch (error) {
    throw archiveError(error as Error);
  }
  const tooManyEntries = () =>
    new ExtensionInstallError(
      "ARCHIVE_TOO_MANY_ENTRIES",
      `The archive has more than ${limits.maxEntries} entries.`,
    );
  if (zipfile.entryCount > limits.maxEntries) {
    zipfile.close();
    throw tooManyEntries();
  }

  let entries = 0;
  let written = 0;
  await new Promise<void>((resolve, reject) => {
    let failed = false;
    const fail = (error: Error) => {
      if (failed) return;
      failed = true;
      if (zipfile.isOpen) zipfile.close();
      reject(error instanceof ExtensionInstallError ? error : archiveError(error));
    };
    zipfile.on("error", fail);
    zipfile.on("end", () => resolve());
    zipfile.on("entry", (entry: Entry) => {
      void (async () => {
        entries += 1;
        if (entries > limits.maxEntries) throw tooManyEntries();
        if (((entry.externalFileAttributes >>> 16) & FILE_TYPE_MASK) === SYMLINK_MODE) {
          throw new ExtensionInstallError(
            "SYMLINK_NOT_ALLOWED",
            `Symbolic links are not allowed: ${entry.fileName}`,
          );
        }
        if (entry.isEncrypted()) {
          throw new ExtensionInstallError(
            "ARCHIVE_ENCRYPTED",
            `Encrypted entries are not supported: ${entry.fileName}`,
          );
        }
        const target = join(destination, ...entry.fileName.split("/"));
        if (entry.fileName.endsWith("/")) {
          await mkdir(target, { recursive: true });
        } else {
          await mkdir(dirname(target), { recursive: true });
          const counter = new Transform({
            transform(chunk: Buffer, _encoding, callback) {
              written += chunk.length;
              if (written > limits.maxBytes) {
                callback(
                  new ExtensionInstallError(
                    "ARCHIVE_TOO_LARGE",
                    `The extracted archive exceeds ${limits.maxBytes} bytes.`,
                  ),
                );
                return;
              }
              callback(null, chunk);
            },
          });
          await pipeline(await openEntryStream(zipfile, entry), counter, createWriteStream(target));
        }
        zipfile.readEntry();
      })().catch(fail);
    });
    zipfile.readEntry();
  });
}

/** 压缩包根目录，或根目录下唯一的目录（忽略 `__MACOSX/`）。The archive root, or its only directory (ignoring `__MACOSX/`). */
async function findArchiveRoot(extracted: string): Promise<string> {
  if (await exists(join(extracted, "manifest.json"))) return extracted;
  const names = (await readdir(extracted)).filter((name) => name !== "__MACOSX");
  if (names.length === 1) {
    const candidate = join(extracted, names[0]);
    if ((await stat(candidate)).isDirectory() && (await exists(join(candidate, "manifest.json")))) {
      return candidate;
    }
  }
  throw new ExtensionInstallError(
    "MANIFEST_NOT_AT_ROOT",
    "manifest.json must be at the archive root or inside its only top-level folder.",
  );
}
