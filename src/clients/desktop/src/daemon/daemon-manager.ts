import { execFile } from "node:child_process";
import { access, mkdir, rm, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { promisify } from "node:util";

import {
  buildLaunchAgentPlist,
  buildSystemdUserUnit,
  buildWindowsTaskArguments,
} from "./registration";

const execFileAsync = promisify(execFile);

export class DaemonManager {
  public constructor(
    private readonly platform: NodeJS.Platform,
    private readonly serverPath: string,
  ) {}

  public async isServerBundled(): Promise<boolean> {
    try {
      await access(this.serverPath);
      return true;
    } catch {
      return false;
    }
  }

  public async install(): Promise<void> {
    if (!(await this.isServerBundled())) throw new Error("Bundled Agw Server was not found.");
    if (this.platform === "darwin") {
      await this.installLaunchAgent();
      return;
    }
    if (this.platform === "linux") {
      await this.installSystemdUserService();
      return;
    }
    if (this.platform === "win32") {
      await execFileAsync("schtasks.exe", buildWindowsTaskArguments(this.serverPath));
      await execFileAsync("schtasks.exe", ["/Run", "/TN", "Agw Server"]);
      return;
    }
    throw new Error(`Unsupported desktop platform: ${this.platform}`);
  }

  public async uninstall(): Promise<void> {
    if (this.platform === "darwin") {
      const plist = this.launchAgentPath();
      await this.runIgnoringFailure("launchctl", [
        "bootout",
        `gui/${process.getuid?.() ?? 0}`,
        plist,
      ]);
      await rm(plist, { force: true });
      return;
    }
    if (this.platform === "linux") {
      await this.runIgnoringFailure("systemctl", [
        "--user",
        "disable",
        "--now",
        "agw-server.service",
      ]);
      await rm(this.systemdUnitPath(), { force: true });
      await this.runIgnoringFailure("systemctl", ["--user", "daemon-reload"]);
      return;
    }
    if (this.platform === "win32") {
      await this.runIgnoringFailure("schtasks.exe", ["/End", "/TN", "Agw Server"]);
      await this.runIgnoringFailure("schtasks.exe", ["/Delete", "/F", "/TN", "Agw Server"]);
    }
  }

  private async installLaunchAgent(): Promise<void> {
    const plist = this.launchAgentPath();
    await mkdir(dirname(plist), { recursive: true });
    await this.runIgnoringFailure("launchctl", [
      "bootout",
      `gui/${process.getuid?.() ?? 0}`,
      plist,
    ]);
    await writeFile(plist, buildLaunchAgentPlist(this.serverPath), {
      encoding: "utf8",
      mode: 0o600,
    });
    await execFileAsync("launchctl", ["bootstrap", `gui/${process.getuid?.() ?? 0}`, plist]);
  }

  private async installSystemdUserService(): Promise<void> {
    const unit = this.systemdUnitPath();
    await mkdir(dirname(unit), { recursive: true });
    await writeFile(unit, buildSystemdUserUnit(this.serverPath), { encoding: "utf8", mode: 0o600 });
    await execFileAsync("systemctl", ["--user", "daemon-reload"]);
    await execFileAsync("systemctl", ["--user", "enable", "--now", "agw-server.service"]);
  }

  private launchAgentPath(): string {
    return join(homedir(), "Library", "LaunchAgents", "com.agw.server.plist");
  }

  private systemdUnitPath(): string {
    return join(homedir(), ".config", "systemd", "user", "agw-server.service");
  }

  private async runIgnoringFailure(command: string, args: string[]): Promise<void> {
    try {
      await execFileAsync(command, args);
    } catch {
      // Removing an absent registration is idempotent.
    }
  }
}
