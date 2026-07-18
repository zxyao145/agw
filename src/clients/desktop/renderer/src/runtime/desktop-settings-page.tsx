"use client";

import * as React from "react";
import { HardDrive, Save } from "lucide-react";
import { toast } from "sonner";

import { Button } from "@agw/components";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@agw/components";
import { Label } from "@agw/components";
import { ServerProfilesPanel } from "./components/server-profiles-panel";
import { useDesktopRuntime } from "./runtime-provider";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@agw/components";

function DesktopSettingsPanel() {
  const desktop = useDesktopRuntime();
  const settings = desktop.runtimeState?.settings;
  const [closeBehavior, setCloseBehavior] = React.useState(
    settings?.closeBehavior ?? "minimize-to-tray",
  );
  const [busy, setBusy] = React.useState(false);
  const [uninstallOpen, setUninstallOpen] = React.useState(false);
  const [deleteServerData, setDeleteServerData] = React.useState(false);

  React.useEffect(() => {
    if (!settings) return;
    setCloseBehavior(settings.closeBehavior);
  }, [settings]);

  if (!settings) {
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
      await desktop.saveSettings({
        ...settings,
        closeBehavior: closeBehavior as "minimize-to-tray" | "quit-desktop",
      });
      toast.success("App settings saved");
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to save Desktop settings");
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
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
      <ServerProfilesPanel />
      <DesktopSettingsPanel />
    </div>
  );
}
