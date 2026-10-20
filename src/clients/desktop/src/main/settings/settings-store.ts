import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { z } from "zod";

import type {
  DesktopSettings,
  DesktopSettingsUpdate,
  ExtensionGrants,
  ExtensionSettings,
  PackageFlavor,
} from "../../shared/contracts";
import { HOST_PERMISSION_PATTERN } from "../../shared/extension-host-permissions";
import { EXTENSION_PERMISSIONS } from "@agw/extension-sdk";
import { writeJsonFile } from "./json-file";
import { DEFAULT_LOCAL_PROFILE, validateServerProfiles } from "./server-profiles";

const extensionSettingsSchema = z.record(
  z.string(),
  z.strictObject({
    enabled: z.boolean(),
    grantedPermissions: z.array(z.enum(EXTENSION_PERMISSIONS)),
    grantedHostPermissions: z.array(z.string().regex(HOST_PERMISSION_PATTERN)),
    lastActivatedVersion: z.string().nullable(),
  }),
);

const fileTypeSchema = z.string().min(1);
const fileViewerIdSchema = z.string().min(1);
const fileViewerDefaultsSchema = z.record(fileTypeSchema, fileViewerIdSchema);

/** 声明集合全部包含在已批准集合中。Every declared entry is already approved. */
export function isGrantCovered(declared: ExtensionGrants, settings: ExtensionSettings): boolean {
  return (
    declared.permissions.every((permission) => settings.grantedPermissions.includes(permission)) &&
    declared.hostPermissions.every((host) => settings.grantedHostPermissions.includes(host))
  );
}

export type SecretCodec = {
  encrypt(value: string): Buffer;
  decrypt(value: Buffer): string;
};

type SecretFile = {
  schemaVersion: 1;
  tokens: Record<string, string>;
  oidcProfiles?: Record<string, boolean>;
};

export class DesktopSettingsStore {
  private readonly settingsFile: string;
  private readonly secretsFile: string;
  private pendingWrite: Promise<void> = Promise.resolve();

  public constructor(
    directory: string,
    private readonly packageFlavor: PackageFlavor,
    private readonly secretCodec: SecretCodec,
  ) {
    this.settingsFile = join(directory, "settings.json");
    this.secretsFile = join(directory, "secrets.json");
  }

  public async load(): Promise<DesktopSettings> {
    try {
      const parsed = JSON.parse(
        await readFile(this.settingsFile, "utf8"),
      ) as Partial<DesktopSettings>;
      const profiles = parsed.profiles ?? [DEFAULT_LOCAL_PROFILE];
      validateServerProfiles(profiles);
      const closeBehavior =
        parsed.closeBehavior === "quit-desktop" ? "quit-desktop" : "minimize-to-tray";
      const activeServerId = profiles.some((profile) => profile.id === parsed.activeServerId)
        ? parsed.activeServerId!
        : "local";
      return {
        schemaVersion: 1,
        packageFlavor: this.packageFlavor,
        closeBehavior,
        profiles,
        activeServerId,
        projectTabsByServer: parsed.projectTabsByServer ?? {},
        extensions: extensionSettingsSchema.parse(parsed.extensions ?? {}),
        removedBundledExtensions: z.array(z.string()).parse(parsed.removedBundledExtensions ?? []),
        fileViewerDefaults: fileViewerDefaultsSchema.parse(parsed.fileViewerDefaults ?? {}),
      };
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
      return this.createDefaults();
    }
  }

  public async save(update: DesktopSettingsUpdate): Promise<void> {
    await this.enqueueWrite(async () => {
      const current = await this.load();
      const profiles = update.profiles ?? current.profiles;
      validateServerProfiles(profiles);
      const profileIds = new Set(profiles.map((profile) => profile.id));
      const activeServerId =
        update.activeServerId ??
        (profileIds.has(current.activeServerId) ? current.activeServerId : "local");
      if (!profileIds.has(activeServerId)) {
        throw new Error("The active Server profile does not exist.");
      }
      const projectTabsByServer = Object.fromEntries(
        Object.entries({ ...current.projectTabsByServer, ...update.projectTabsByServer }).filter(
          ([profileId]) => profileIds.has(profileId),
        ),
      );
      if (update.profiles) {
        const secrets = await this.loadSecretFile();
        let changed = false;
        for (const oldProfile of current.profiles) {
          const next = profiles.find((profile) => profile.id === oldProfile.id);
          if (
            secrets.oidcProfiles?.[oldProfile.id] &&
            (!next || next.baseUrl !== oldProfile.baseUrl)
          ) {
            delete secrets.tokens[oldProfile.id];
            if (!next) delete secrets.oidcProfiles[oldProfile.id];
            changed = true;
          }
        }
        if (changed) await writeJsonFile(this.secretsFile, secrets);
      }
      await writeJsonFile(this.settingsFile, {
        ...current,
        closeBehavior: update.closeBehavior ?? current.closeBehavior,
        profiles,
        activeServerId,
        projectTabsByServer,
      });
    });
  }

  public async saveToken(
    profileId: string,
    token: string,
    source: "manual" | "oidc" = "manual",
    beforeCommit?: () => Promise<void>,
  ): Promise<void> {
    if (!token.startsWith("agw_")) throw new Error("Agw API tokens must start with agw_.");
    await this.enqueueWrite(async () => {
      const secrets = await this.loadSecretFile();
      secrets.tokens[profileId] = this.secretCodec.encrypt(token).toString("base64");
      secrets.oidcProfiles ??= {};
      if (source === "oidc") secrets.oidcProfiles[profileId] = true;
      else delete secrets.oidcProfiles[profileId];
      await writeJsonFile(this.secretsFile, secrets, beforeCommit);
    });
  }

  /** 启用时写入批准结果；停用时保留批准结果。Enabling stores the grants; disabling keeps them. */
  public async setExtensionEnabled(
    extensionId: string,
    enabled: boolean,
    grants?: ExtensionGrants,
  ): Promise<void> {
    await this.updateExtensions((extensions) => {
      const current = extensions[extensionId];
      if (enabled) {
        if (!grants) throw new Error("Enabling an extension requires its grants.");
        extensions[extensionId] = {
          enabled: true,
          grantedPermissions: [...grants.permissions],
          grantedHostPermissions: [...grants.hostPermissions],
          lastActivatedVersion: current?.lastActivatedVersion ?? null,
        };
      } else if (current) {
        extensions[extensionId] = { ...current, enabled: false };
      }
    });
  }

  public async recordExtensionActivation(extensionId: string, version: string): Promise<void> {
    await this.updateExtensions((extensions) => {
      const current = extensions[extensionId];
      if (!current) throw new Error(`Extension ${extensionId} has no settings.`);
      extensions[extensionId] = { ...current, lastActivatedVersion: version };
    });
  }

  /**
   * 替换安装后按新声明更新：新声明都已批准时保持启用状态并收窄批准集合，否则停用等待重新批准。
   * After a replacement install: keep the enabled state and narrow the grants when everything is approved; otherwise disable until approved again.
   */
  public async applyInstalledExtension(
    extensionId: string,
    declared: ExtensionGrants,
  ): Promise<void> {
    await this.updateExtensions((extensions) => {
      const current = extensions[extensionId];
      if (!current) return;
      extensions[extensionId] = isGrantCovered(declared, current)
        ? {
            ...current,
            grantedPermissions: [...declared.permissions],
            grantedHostPermissions: [...declared.hostPermissions],
          }
        : { ...current, enabled: false };
    });
  }

  /**
   * 保证"启用时批准集合等于当前声明"：声明增加时停用，声明减少时收窄。
   * Keeps "enabled grants equal the current declaration": additions disable, removals narrow.
   */
  public async reconcileExtensionGrants(
    declared: ReadonlyMap<string, ExtensionGrants>,
  ): Promise<void> {
    await this.updateExtensions((extensions) => {
      for (const [extensionId, grants] of declared) {
        const current = extensions[extensionId];
        if (!current?.enabled) continue;
        extensions[extensionId] = isGrantCovered(grants, current)
          ? {
              ...current,
              grantedPermissions: [...grants.permissions],
              grantedHostPermissions: [...grants.hostPermissions],
            }
          : { ...current, enabled: false };
      }
    });
  }

  public async removeExtension(extensionId: string): Promise<void> {
    await this.updateExtensions((extensions) => {
      delete extensions[extensionId];
    });
  }

  /**
   * 内置 extension：没有设置项且未被卸载时默认启用并批准声明；已有设置项时把批准集合更新为当前声明，保持启用状态。
   * Bundled extensions: without an entry and not uninstalled they are enabled with the declaration approved;
   * existing entries get the current declaration approved and keep their enabled state.
   */
  public async applyBundledExtensions(
    declared: ReadonlyMap<string, ExtensionGrants>,
  ): Promise<void> {
    await this.updateExtensionState((state) => {
      for (const [extensionId, grants] of declared) {
        if (state.removedBundledExtensions.includes(extensionId)) continue;
        const current = state.extensions[extensionId];
        state.extensions[extensionId] = {
          enabled: current?.enabled ?? true,
          grantedPermissions: [...grants.permissions],
          grantedHostPermissions: [...grants.hostPermissions],
          lastActivatedVersion: current?.lastActivatedVersion ?? null,
        };
      }
    });
  }

  /** 卸载内置 extension：记录 id 并删除设置项。Uninstalling a bundled extension records its id and deletes its entry. */
  public async removeBundledExtension(extensionId: string): Promise<void> {
    await this.updateExtensionState((state) => {
      delete state.extensions[extensionId];
      if (!state.removedBundledExtensions.includes(extensionId)) {
        state.removedBundledExtensions.push(extensionId);
      }
    });
  }

  /**
   * 设置文件类型的默认查看器，`viewerId` 为 null 时删除该项；返回更新后的全部设置。
   * Sets the default viewer of a file type, or removes the entry when `viewerId` is null; returns every setting after
   * the change.
   */
  public async setFileViewerDefault(
    fileType: string,
    viewerId: string | null,
  ): Promise<Record<string, string>> {
    fileTypeSchema.parse(fileType);
    fileViewerIdSchema.nullable().parse(viewerId);
    let defaults: Record<string, string> = {};
    await this.enqueueWrite(async () => {
      const current = await this.load();
      defaults = Object.fromEntries([
        ...Object.entries(current.fileViewerDefaults).filter(([key]) => key !== fileType),
        ...(viewerId === null ? [] : [[fileType, viewerId]]),
      ]);
      await writeJsonFile(this.settingsFile, { ...current, fileViewerDefaults: defaults });
    });
    return defaults;
  }

  private async updateExtensions(
    change: (extensions: Record<string, ExtensionSettings>) => void,
  ): Promise<void> {
    await this.updateExtensionState((state) => change(state.extensions));
  }

  private async updateExtensionState(
    change: (state: Pick<DesktopSettings, "extensions" | "removedBundledExtensions">) => void,
  ): Promise<void> {
    await this.enqueueWrite(async () => {
      const current = await this.load();
      const state = structuredClone({
        extensions: current.extensions,
        removedBundledExtensions: current.removedBundledExtensions,
      });
      change(state);
      if (
        JSON.stringify(state.extensions) === JSON.stringify(current.extensions) &&
        JSON.stringify(state.removedBundledExtensions) ===
          JSON.stringify(current.removedBundledExtensions)
      ) {
        return;
      }
      await writeJsonFile(this.settingsFile, { ...current, ...state });
    });
  }

  public async credentialSource(profileId: string): Promise<"manual" | "oidc"> {
    return (await this.loadSecretFile()).oidcProfiles?.[profileId] ? "oidc" : "manual";
  }

  public async loadToken(profileId: string): Promise<string | null> {
    const secrets = await this.loadSecretFile();
    const encrypted = secrets.tokens[profileId];
    return encrypted ? this.secretCodec.decrypt(Buffer.from(encrypted, "base64")) : null;
  }

  public async deleteToken(profileId: string): Promise<void> {
    await this.enqueueWrite(async () => {
      const secrets = await this.loadSecretFile();
      delete secrets.tokens[profileId];
      await writeJsonFile(this.secretsFile, secrets);
    });
  }

  private createDefaults(): DesktopSettings {
    return {
      schemaVersion: 1,
      packageFlavor: this.packageFlavor,
      closeBehavior: "minimize-to-tray",
      profiles: [DEFAULT_LOCAL_PROFILE],
      activeServerId: "local",
      projectTabsByServer: {},
      extensions: {},
      removedBundledExtensions: [],
      fileViewerDefaults: {},
    };
  }

  private async loadSecretFile(): Promise<SecretFile> {
    try {
      const parsed = JSON.parse(await readFile(this.secretsFile, "utf8")) as Partial<SecretFile>;
      return {
        schemaVersion: 1,
        tokens: parsed.tokens ?? {},
        oidcProfiles: parsed.oidcProfiles ?? {},
      };
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
      return { schemaVersion: 1, tokens: {} };
    }
  }

  private enqueueWrite(write: () => Promise<void>): Promise<void> {
    const pending = this.pendingWrite.then(write);
    this.pendingWrite = pending.catch(() => undefined);
    return pending;
  }
}
