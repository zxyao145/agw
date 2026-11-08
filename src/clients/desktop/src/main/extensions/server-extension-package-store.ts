import { createHash, randomUUID } from "node:crypto";
import type { Dirent } from "node:fs";
import { mkdir, readFile, readdir, rename, rm, stat, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { isDeepStrictEqual } from "node:util";

import type { ServerExtensionCatalogRelease, ServerExtensionOrigin } from "../../shared/contracts";
import { readExtensionDirectory, type CatalogEntry } from "./extension-catalog";
import { extensionInstanceId } from "./extension-instance";
import { extractServerExtensionArchive } from "./extension-installer";

const SHA256_PATTERN = /^[0-9a-f]{64}$/u;

type CachedRelease = {
  extensionId: string;
  releaseHash: string;
};

type CachedEntry = CatalogEntry & {
  instanceId: string;
  origin: ServerExtensionOrigin;
  releaseHash: string;
  source: "server";
};

class CorruptCachedReleaseError extends Error {}

function isMissingCacheFile(error: unknown): boolean {
  const code = (error as NodeJS.ErrnoException).code;
  return code === "ENOENT" || code === "ENOTDIR";
}

/** 按内容保存经过校验的 release，各实例分别维护来源。Stores verified release content with per-instance source identity. */
export class ServerExtensionPackageStore {
  private pending: Promise<unknown> = Promise.resolve();
  private readonly validatedEntries = new Map<string, CachedEntry>();

  public constructor(
    private readonly directory: string,
    private readonly appVersion: string,
  ) {}

  public instanceId(origin: ServerExtensionOrigin, extensionId: string): string {
    return extensionInstanceId(origin, extensionId);
  }

  public has(
    origin: ServerExtensionOrigin,
    extensionId: string,
    releaseHash: string,
  ): Promise<boolean> {
    return this.serialize(async () => {
      const releaseDirectory = this.releaseDirectory(releaseHash);
      const key = this.cacheKey(origin, extensionId, releaseHash);
      try {
        await stat(releaseDirectory);
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code === "ENOENT") {
          this.validatedEntries.delete(key);
          return false;
        }
        throw error;
      }
      try {
        this.validatedEntries.set(
          key,
          await this.readCachedRelease(origin, extensionId, releaseHash),
        );
        return true;
      } catch (error) {
        if (!(error instanceof CorruptCachedReleaseError)) throw error;
        this.validatedEntries.delete(key);
        await rm(releaseDirectory, { recursive: true, force: true });
        return false;
      }
    });
  }

  public load(
    origin: ServerExtensionOrigin,
    extensionId: string,
    releaseHash: string,
  ): Promise<CachedEntry> {
    return this.serialize(async () => {
      const key = this.cacheKey(origin, extensionId, releaseHash);
      const cached = this.validatedEntries.get(key);
      if (cached) return cached;
      try {
        const entry = await this.readCachedRelease(origin, extensionId, releaseHash);
        this.validatedEntries.set(key, entry);
        return entry;
      } catch (error) {
        if (error instanceof CorruptCachedReleaseError) {
          await rm(this.releaseDirectory(releaseHash), { recursive: true, force: true });
        }
        throw error;
      }
    });
  }

  public cache(
    origin: ServerExtensionOrigin,
    release: ServerExtensionCatalogRelease,
    archive: Uint8Array,
  ): Promise<CachedEntry> {
    return this.serialize(async () => {
      if (!SHA256_PATTERN.test(release.releaseHash) || !SHA256_PATTERN.test(release.packageHash)) {
        throw new Error("The Server catalog contains an invalid package hash.");
      }
      const archiveHash = createHash("sha256").update(archive).digest("hex");
      if (archiveHash !== release.packageHash) {
        throw new Error("The downloaded extension package does not match its catalog hash.");
      }
      const cached = await this.readCachedIfPresent(origin, release.id, release.releaseHash);
      if (cached) return cached;

      const stagingDirectory = join(this.directory, ".staging", randomUUID());
      const archiveFile = join(stagingDirectory, "package.zip");
      const extractedDirectory = join(stagingDirectory, "extracted");
      try {
        await mkdir(stagingDirectory, { recursive: true });
        await writeFile(archiveFile, archive);
        const packageRoot = await extractServerExtensionArchive(archiveFile, extractedDirectory);
        const manifestBytes = await readFile(join(packageRoot, "manifest.json"));
        const rawManifest = JSON.parse(manifestBytes.toString("utf8"));
        const manifestHash = createHash("sha256").update(manifestBytes).digest("hex");
        if (manifestHash !== release.releaseHash) {
          throw new Error(
            "The downloaded extension manifest digest does not match its catalog release hash.",
          );
        }
        if (!isDeepStrictEqual(rawManifest, release.manifest)) {
          throw new Error("The downloaded extension manifest does not match the Server catalog.");
        }
        const entry = await readExtensionDirectory(packageRoot, this.appVersion, null, undefined, {
          serverProjection: true,
        });
        if (entry.validation !== "valid" || !entry.manifest) {
          const details = entry.errors.map((item) => item.message).join(" ");
          throw new Error("The downloaded extension package is invalid. " + details);
        }
        if (
          entry.manifest.manifestVersion !== 2 ||
          entry.manifest.id !== release.id ||
          entry.manifest.name !== release.name ||
          entry.manifest.displayName !== release.displayName ||
          entry.manifest.version !== release.version ||
          ((entry.manifest.targets ?? 0) & 2) === 0
        ) {
          throw new Error("The downloaded extension package metadata does not match its catalog.");
        }

        const cached = await this.readCachedIfPresent(origin, release.id, release.releaseHash);
        if (cached) return cached;

        const stagedReleaseDirectory = join(stagingDirectory, "release");
        await mkdir(stagedReleaseDirectory, { recursive: true });
        await rename(packageRoot, join(stagedReleaseDirectory, release.id));
        const metadata: CachedRelease = {
          extensionId: release.id,
          releaseHash: release.releaseHash,
        };
        await writeFile(
          join(stagedReleaseDirectory, "release.json"),
          JSON.stringify(metadata) + "\n",
          "utf8",
        );
        const releaseDirectory = this.releaseDirectory(release.releaseHash);
        await mkdir(this.directory, { recursive: true });
        await rename(stagedReleaseDirectory, releaseDirectory);
        const cachedEntry = await this.readCachedRelease(origin, release.id, release.releaseHash);
        this.validatedEntries.set(
          this.cacheKey(origin, release.id, release.releaseHash),
          cachedEntry,
        );
        return cachedEntry;
      } finally {
        await rm(stagingDirectory, { recursive: true, force: true });
      }
    });
  }

  private releaseDirectory(releaseHash: string): string {
    if (!SHA256_PATTERN.test(releaseHash)) throw new Error("Invalid extension release hash.");
    return join(this.directory, releaseHash);
  }

  private async readCachedIfPresent(
    origin: ServerExtensionOrigin,
    extensionId: string,
    releaseHash: string,
  ): Promise<CachedEntry | null> {
    const releaseDirectory = this.releaseDirectory(releaseHash);
    try {
      await stat(releaseDirectory);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === "ENOENT") return null;
      throw error;
    }
    const key = this.cacheKey(origin, extensionId, releaseHash);
    try {
      const entry = await this.readCachedRelease(origin, extensionId, releaseHash);
      this.validatedEntries.set(key, entry);
      return entry;
    } catch (error) {
      if (!(error instanceof CorruptCachedReleaseError)) throw error;
      this.validatedEntries.delete(key);
      await rm(releaseDirectory, { recursive: true, force: true });
      return null;
    }
  }

  private async readCachedRelease(
    origin: ServerExtensionOrigin,
    extensionId: string,
    releaseHash: string,
  ): Promise<CachedEntry> {
    const releaseDirectory = this.releaseDirectory(releaseHash);
    let metadataText: string;
    try {
      metadataText = await readFile(join(releaseDirectory, "release.json"), "utf8");
    } catch (error) {
      if (isMissingCacheFile(error)) {
        throw new CorruptCachedReleaseError("The cached extension release metadata is missing.");
      }
      throw error;
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(metadataText);
    } catch (error) {
      if (!(error instanceof SyntaxError)) throw error;
      throw new CorruptCachedReleaseError("The cached extension release metadata is invalid.");
    }
    if (typeof parsed !== "object" || parsed === null) {
      throw new CorruptCachedReleaseError("The cached extension release metadata is invalid.");
    }
    const metadata = parsed as CachedRelease;
    if (metadata.extensionId !== extensionId || metadata.releaseHash !== releaseHash) {
      throw new CorruptCachedReleaseError("The cached extension release metadata is invalid.");
    }
    const extensionDirectory = join(releaseDirectory, extensionId);
    let manifestBytes: Buffer;
    try {
      manifestBytes = await readFile(join(extensionDirectory, "manifest.json"));
    } catch (error) {
      if (isMissingCacheFile(error)) {
        throw new CorruptCachedReleaseError("The cached extension manifest is missing.");
      }
      throw error;
    }
    if (createHash("sha256").update(manifestBytes).digest("hex") !== releaseHash) {
      throw new CorruptCachedReleaseError(
        "The cached extension manifest digest does not match its release hash.",
      );
    }
    let entry: CatalogEntry;
    try {
      entry = await readExtensionDirectory(
        extensionDirectory,
        this.appVersion,
        extensionId,
        undefined,
        { serverProjection: true },
      );
    } catch (error) {
      if (isMissingCacheFile(error)) {
        throw new CorruptCachedReleaseError("The cached extension package is incomplete.");
      }
      throw error;
    }
    if (entry.validation !== "valid" || !entry.manifest) {
      const details = entry.errors.map((item) => item.message).join(" ");
      throw new CorruptCachedReleaseError("The cached extension release is invalid. " + details);
    }
    return {
      ...entry,
      instanceId: this.instanceId(origin, extensionId),
      origin,
      releaseHash,
      source: "server",
    };
  }

  private cacheKey(
    origin: ServerExtensionOrigin,
    extensionId: string,
    releaseHash: string,
  ): string {
    return JSON.stringify([origin.serverId, origin.userId, extensionId, releaseHash]);
  }

  private serialize<T>(operation: () => Promise<T>): Promise<T> {
    const result = this.pending.then(operation, operation);
    this.pending = result.then(
      () => undefined,
      () => undefined,
    );
    return result;
  }

  public initialize(): Promise<void> {
    return this.serialize(() =>
      rm(join(this.directory, ".staging"), { recursive: true, force: true }),
    );
  }

  public prune(retainedReleaseHashes: ReadonlySet<string>): Promise<void> {
    return this.serialize(async () => {
      let entries: Dirent[];
      try {
        entries = await readdir(this.directory, { withFileTypes: true });
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code === "ENOENT") return;
        throw error;
      }
      for (const entry of entries) {
        if (
          !entry.isDirectory() ||
          !SHA256_PATTERN.test(entry.name) ||
          retainedReleaseHashes.has(entry.name)
        ) {
          continue;
        }
        await rm(join(this.directory, entry.name), { recursive: true, force: true });
        for (const [key, cached] of this.validatedEntries) {
          if (cached.releaseHash === entry.name) this.validatedEntries.delete(key);
        }
      }
    });
  }
}
