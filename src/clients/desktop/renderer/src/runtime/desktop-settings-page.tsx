"use client";

import * as React from "react";
import { HardDrive, Save, Server } from "lucide-react";
import { toast } from "sonner";

import type { ServerProfile } from "@desktop/shared/contracts";
import { Button } from "@agw/components";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@agw/components";
import { Input } from "@agw/components";
import { Label } from "@agw/components";
import { useDesktopRuntime } from "./runtime-provider";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@agw/components";

function normalizeHttpUrl(value: string): string {
  const url = new URL(value.trim());
  if (url.protocol !== "http:" && url.protocol !== "https:") {
    throw new Error("Server URL must use HTTP or HTTPS.");
  }
  url.pathname = url.pathname.replace(/\/+$/u, "");
  url.search = "";
  url.hash = "";
  return url.toString().replace(/\/$/u, "");
}

function DesktopSettingsPanel() {
  const desktop = useDesktopRuntime();
  const settings = desktop.runtimeState?.settings;
  const localProfile = settings?.profiles.find((profile) => profile.kind === "local");
  const savedRemote = settings?.profiles.find((profile) => profile.kind === "remote");
  const [localUrl, setLocalUrl] = React.useState(localProfile?.baseUrl ?? "http://127.0.0.1:30815");
  const [remoteEnabled, setRemoteEnabled] = React.useState(Boolean(savedRemote));
  const [remoteName, setRemoteName] = React.useState(savedRemote?.name ?? "Remote");
  const [remoteUrl, setRemoteUrl] = React.useState(savedRemote?.baseUrl ?? "https://");
  const [remoteToken, setRemoteToken] = React.useState("");
  const [allowInsecureHttp, setAllowInsecureHttp] = React.useState(
    savedRemote?.allowInsecureHttp ?? false,
  );
  const [activeServerId, setActiveServerId] = React.useState(settings?.activeServerId ?? "local");
  const [closeBehavior, setCloseBehavior] = React.useState(
    settings?.closeBehavior ?? "minimize-to-tray",
  );
  const [busy, setBusy] = React.useState(false);
  const [uninstallOpen, setUninstallOpen] = React.useState(false);
  const [deleteServerData, setDeleteServerData] = React.useState(false);

  React.useEffect(() => {
    if (!settings) return;
    const nextLocal = settings.profiles.find((profile) => profile.kind === "local");
    const nextRemote = settings.profiles.find((profile) => profile.kind === "remote");
    setLocalUrl(nextLocal?.baseUrl ?? "http://127.0.0.1:30815");
    setRemoteEnabled(Boolean(nextRemote));
    setRemoteName(nextRemote?.name ?? "Remote");
    setRemoteUrl(nextRemote?.baseUrl ?? "https://");
    setAllowInsecureHttp(nextRemote?.allowInsecureHttp ?? false);
    setActiveServerId(settings.activeServerId);
    setCloseBehavior(settings.closeBehavior);
  }, [settings]);

  if (!settings || !localProfile) {
    return (
      <Card>
        <CardContent className="py-6 text-sm text-muted-foreground">
          Loading Desktop settings…
        </CardContent>
      </Card>
    );
  }

  const handleSave = async () => {
    setBusy(true);
    try {
      const profiles: ServerProfile[] = [{ ...localProfile, baseUrl: normalizeHttpUrl(localUrl) }];
      if (remoteEnabled) {
        const normalizedRemoteUrl = normalizeHttpUrl(remoteUrl);
        if (normalizedRemoteUrl.startsWith("http://") && !allowInsecureHttp) {
          throw new Error("Remote HTTP requires explicit consent.");
        }
        profiles.push({
          id: "remote",
          kind: "remote",
          name: remoteName.trim() || "Remote",
          baseUrl: normalizedRemoteUrl,
          apiMajorVersion: 1,
          allowInsecureHttp,
        });
        if (remoteToken.trim()) {
          await window.agwDesktop?.saveToken("remote", remoteToken.trim());
        }
      } else {
        await window.agwDesktop?.deleteToken("remote");
      }

      await desktop.saveSettings({
        ...settings,
        closeBehavior: closeBehavior as "minimize-to-tray" | "quit-desktop",
        profiles,
        activeServerId: remoteEnabled && activeServerId === "remote" ? "remote" : "local",
      });
      setRemoteToken("");
      toast.success("Desktop settings saved");
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to save Desktop settings");
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <Card id="local-server" className="scroll-mt-4">
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Server className="h-5 w-5" /> Server connections
          </CardTitle>
          <CardDescription>
            Desktop keeps one local profile and, optionally, one remote profile. Remote HTTP is
            disabled until you explicitly allow it.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-5">
          <div className="grid gap-2">
            <Label htmlFor="active-server">Active Server</Label>
            <select
              id="active-server"
              className="h-9 rounded-md border bg-background px-3 text-sm"
              value={activeServerId}
              onChange={(event) => setActiveServerId(event.target.value)}
            >
              <option value="local">Local</option>
              {remoteEnabled ? <option value="remote">{remoteName || "Remote"}</option> : null}
            </select>
          </div>
          <div className="grid gap-2">
            <Label htmlFor="local-server-url">Local Server URL</Label>
            <Input
              id="local-server-url"
              value={localUrl}
              onChange={(event) => setLocalUrl(event.target.value)}
            />
            <p className="text-xs text-muted-foreground">
              The bundled daemon defaults to http://127.0.0.1:30815. Use this field when the local
              listener is configured differently.
            </p>
          </div>

          <label className="flex items-center gap-2 text-sm font-medium">
            <input
              type="checkbox"
              checked={remoteEnabled}
              onChange={(event) => setRemoteEnabled(event.target.checked)}
            />
            Configure a remote Server
          </label>
          {remoteEnabled ? (
            <div className="grid gap-4 rounded-xl border bg-muted/25 p-4 sm:grid-cols-2">
              <div className="grid gap-2">
                <Label htmlFor="remote-server-name">Name</Label>
                <Input
                  id="remote-server-name"
                  value={remoteName}
                  onChange={(event) => setRemoteName(event.target.value)}
                />
              </div>
              <div className="grid gap-2">
                <Label htmlFor="remote-server-url">Server URL</Label>
                <Input
                  id="remote-server-url"
                  value={remoteUrl}
                  onChange={(event) => setRemoteUrl(event.target.value)}
                />
              </div>
              <div className="grid gap-2 sm:col-span-2">
                <Label htmlFor="remote-server-token">API token</Label>
                <Input
                  id="remote-server-token"
                  type="password"
                  value={remoteToken}
                  placeholder="Leave blank to keep the saved token"
                  onChange={(event) => setRemoteToken(event.target.value)}
                />
              </div>
              <label className="flex items-center gap-2 text-xs text-muted-foreground sm:col-span-2">
                <input
                  type="checkbox"
                  checked={allowInsecureHttp}
                  onChange={(event) => setAllowInsecureHttp(event.target.checked)}
                />
                I understand that remote HTTP exposes the API token and traffic on the network.
              </label>
            </div>
          ) : null}
        </CardContent>
      </Card>

      <Card id="appearance" className="scroll-mt-4">
        <CardHeader>
          <CardTitle>Desktop behavior</CardTitle>
          <CardDescription>
            The Server daemon continues when the Desktop window closes.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid gap-2">
            <Label htmlFor="close-behavior">When closing the window</Label>
            <select
              id="close-behavior"
              className="h-9 rounded-md border bg-background px-3 text-sm"
              value={closeBehavior}
              onChange={(event) => setCloseBehavior(event.target.value as typeof closeBehavior)}
            >
              <option value="minimize-to-tray">Minimize to tray (default)</option>
              <option value="quit-desktop">Quit Desktop</option>
            </select>
          </div>
          <div
            id="about"
            className="flex scroll-mt-4 flex-wrap items-center justify-between gap-3 rounded-xl border p-4"
          >
            <div>
              <p className="text-sm font-medium">Package</p>
              <p className="text-xs text-muted-foreground">
                {desktop.runtimeState?.packageFlavor === "full"
                  ? "Full · Desktop and bundled Server daemon"
                  : "Client · Desktop only"}
                {desktop.serverInfo ? ` · Server ${desktop.serverInfo.serverVersion}` : ""}
              </p>
            </div>
            <Button variant="destructive" onClick={() => setUninstallOpen(true)}>
              Uninstall…
            </Button>
          </div>
          <Button disabled={busy} onClick={handleSave}>
            <Save className="h-4 w-4" />
            Save Desktop settings
          </Button>
        </CardContent>
      </Card>

      <Dialog open={uninstallOpen} onOpenChange={setUninstallOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Uninstall Agw Desktop?</DialogTitle>
            <DialogDescription>
              The bundled daemon will be unregistered. Choose whether Server data in ~/agw should be
              retained.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-3 py-2">
            <label className="flex gap-3 rounded-xl border p-3 text-sm">
              <input
                type="radio"
                name="server-data"
                checked={!deleteServerData}
                onChange={() => setDeleteServerData(false)}
              />
              <span>
                <strong>Keep ~/agw</strong>
                <br />
                <span className="text-muted-foreground">Recommended for future reinstalls.</span>
              </span>
            </label>
            <label className="flex gap-3 rounded-xl border border-destructive/30 p-3 text-sm">
              <input
                type="radio"
                name="server-data"
                checked={deleteServerData}
                onChange={() => setDeleteServerData(true)}
              />
              <span>
                <strong>Delete ~/agw</strong>
                <br />
                <span className="text-muted-foreground">
                  Permanently removes configuration, database, skills, keys, and logs.
                </span>
              </span>
            </label>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setUninstallOpen(false)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              onClick={async () => {
                const result = await window.agwDesktop?.prepareUninstall({ deleteServerData });
                if (result) toast.info(result.message);
                setUninstallOpen(false);
              }}
            >
              <HardDrive className="h-4 w-4" /> Prepare uninstall
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}

export function DesktopSettingsPage() {
  return (
    <div className="w-full max-w-4xl space-y-6 py-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <p className="text-xs font-semibold uppercase tracking-[0.12em] text-primary">
            Desktop & Server
          </p>
          <h1 className="mt-1 text-2xl font-semibold">Connections and app</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Manage Server profiles, close behavior, package details, and uninstall data.
          </p>
        </div>
      </div>
      <DesktopSettingsPanel />
    </div>
  );
}
