import { mkdir, readFile, rename, writeFile } from "node:fs/promises";
import { join } from "node:path";

import type { DesktopSettings, DesktopSettingsUpdate, PackageFlavor } from "../../shared/contracts";
import { DEFAULT_LOCAL_PROFILE, validateServerProfiles } from "./server-profiles";

export type SecretCodec = {
  encrypt(value: string): Buffer;
  decrypt(value: Buffer): string;
};

type SecretFile = {
  schemaVersion: 1;
  tokens: Record<string, string>;
};

export class DesktopSettingsStore {
  private readonly settingsFile: string;
  private readonly secretsFile: string;
  private pendingWrite: Promise<void> = Promise.resolve();

  public constructor(
    private readonly directory: string,
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
      await this.writeJson(this.settingsFile, {
        ...current,
        closeBehavior: update.closeBehavior ?? current.closeBehavior,
        profiles,
        activeServerId,
        projectTabsByServer,
      });
    });
  }

  public async saveToken(profileId: string, token: string): Promise<void> {
    if (!token.startsWith("agw_")) throw new Error("Agw API tokens must start with agw_.");
    await this.enqueueWrite(async () => {
      const secrets = await this.loadSecretFile();
      secrets.tokens[profileId] = this.secretCodec.encrypt(token).toString("base64");
      await this.writeJson(this.secretsFile, secrets);
    });
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
      await this.writeJson(this.secretsFile, secrets);
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
    };
  }

  private async loadSecretFile(): Promise<SecretFile> {
    try {
      const parsed = JSON.parse(await readFile(this.secretsFile, "utf8")) as Partial<SecretFile>;
      return { schemaVersion: 1, tokens: parsed.tokens ?? {} };
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

  private async writeJson(file: string, value: unknown): Promise<void> {
    await mkdir(this.directory, { recursive: true });
    const temporaryFile = `${file}.${process.pid}.tmp`;
    await writeFile(temporaryFile, `${JSON.stringify(value, null, 2)}\n`, {
      encoding: "utf8",
      mode: 0o600,
    });
    await rename(temporaryFile, file);
  }
}
