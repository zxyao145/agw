import type {
  ServerExtensionArchive,
  ServerExtensionCatalog,
  ServerExtensionOrigin,
} from "../../shared/contracts";
import type { ServerExtensionPackageStore } from "./server-extension-package-store";
import type { SourcedCatalogEntry } from "./extension-catalog";

type ServerExtensionCatalogFailure = {
  extensionId: string;
  releaseHash: string;
  message: string;
};

export type ResolvedServerExtensionCatalogEntry = {
  instanceId: string;
  releaseHash: string;
  available: boolean;
  entry: SourcedCatalogEntry;
};

function releaseKey(instanceId: string, releaseHash: string): string {
  return instanceId + "\u0000" + releaseHash;
}

export async function resolveServerExtensionCatalogEntries(options: {
  packages: ServerExtensionPackageStore;
  origin: ServerExtensionOrigin;
  catalog: ServerExtensionCatalog;
  archives: readonly ServerExtensionArchive[];
  failures?: readonly ServerExtensionCatalogFailure[];
  previousInstances?: ReadonlyMap<string, SourcedCatalogEntry>;
}): Promise<ResolvedServerExtensionCatalogEntry[]> {
  const releases = new Map<string, (typeof options.catalog.extensions)[number]>();
  for (const release of options.catalog.extensions) {
    const instanceId = options.packages.instanceId(options.origin, release.id);
    const key = releaseKey(instanceId, release.releaseHash);
    if (releases.has(key)) throw new Error("The Server catalog contains a duplicate release.");
    releases.set(key, release);
  }

  const archives = new Map<string, ServerExtensionArchive>();
  for (const archive of options.archives) {
    const instanceId = options.packages.instanceId(options.origin, archive.extensionId);
    const key = releaseKey(instanceId, archive.releaseHash);
    if (!releases.has(key) || archives.has(key)) {
      throw new Error("A downloaded package does not match a unique Server catalog release.");
    }
    archives.set(key, archive);
  }

  const failures = new Map<string, ServerExtensionCatalogFailure>();
  for (const failure of options.failures ?? []) {
    const instanceId = options.packages.instanceId(options.origin, failure.extensionId);
    const key = releaseKey(instanceId, failure.releaseHash);
    if (!releases.has(key) || failures.has(key)) {
      throw new Error("A failed package does not match a unique Server catalog release.");
    }
    failures.set(key, failure);
  }

  const entries: ResolvedServerExtensionCatalogEntry[] = [];
  for (const [key, release] of releases) {
    const instanceId = options.packages.instanceId(options.origin, release.id);
    const archive = archives.get(key);
    try {
      const failure = failures.get(key);
      if (failure) throw new Error(failure.message);
      const packageEntry = archive
        ? await options.packages.cache(options.origin, release, archive.bytes)
        : await options.packages.load(options.origin, release.id, release.releaseHash);
      entries.push({
        instanceId,
        releaseHash: release.releaseHash,
        available: true,
        entry: { ...packageEntry, serverAvailable: true, source: "server" },
      });
    } catch (error) {
      const previous = options.previousInstances?.get(instanceId);
      entries.push({
        instanceId,
        releaseHash: release.releaseHash,
        available: false,
        entry: {
          ...previous,
          id: release.id,
          instanceId,
          origin: options.origin,
          releaseHash: release.releaseHash,
          serverAvailable: false,
          source: "server",
          directory: previous?.directory ?? "",
          manifest: release.manifest,
          validation: "invalid",
          errors: [
            {
              code: "server-package-unavailable",
              message: error instanceof Error ? error.message : String(error),
              path: null,
            },
          ],
          icons: {},
          themes: [],
        },
      });
    }
  }
  return entries;
}
