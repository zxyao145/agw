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
import { EXTENSION_PERMISSIONS } from "../../shared/extension-permissions";
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

  private async updateExtensions(
    change: (extensions: Record<string, ExtensionSettings>) => void,
  ): Promise<void> {
    await this.enqueueWrite(async () => {
      const current = await this.load();
      const extensions = structuredClone(current.extensions);
      change(extensions);
      if (JSON.stringify(extensions) === JSON.stringify(current.extensions)) return;
      await writeJsonFile(this.settingsFile, { ...current, extensions });
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
