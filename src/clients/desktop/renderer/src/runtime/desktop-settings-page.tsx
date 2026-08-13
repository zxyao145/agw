"use client";

import * as React from "react";
import {
  ArrowUpRight,
  CheckCircle2,
  Download,
  ExternalLink,
  HardDrive,
  LoaderCircle,
  RefreshCw,
  Save,
  TriangleAlert,
} from "lucide-react";
import { toast } from "sonner";

import type { DesktopUpdateCheckResult } from "@desktop/shared/contracts";
import { AgwLogo, Badge } from "@agw/components";
import { Button } from "@agw/components";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@agw/components";
import { cn, formatLocalDateTime } from "@agw/components";
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
    <section id="appearance" className="scroll-mt-4 space-y-5">
      <div className="flex flex-col ">
        <p className="text-xs font-semibold uppercase tracking-[0.12em] text-primary">
          Appearance & close
        </p>
        <div className="flex items-start justify-between gap-4">
          <div>
            <h1 className="mt-1 text-2xl font-semibold">Desktop behavior</h1>
            <p className="mt-1 text-sm text-muted-foreground">
              The Server daemon continues when the Desktop window closes.
            </p>
          </div>
        </div>
      </div>

      <Card className="gap-0 overflow-hidden p-4">
        <CardContent className="space-y-4 px-0">
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
          <div className="flex justify-end">
            <Button disabled={busy} onClick={handleSave}>
              <Save className="h-4 w-4" />
              Save Desktop settings
            </Button>
          </div>
        </CardContent>
      </Card>
    </section>
  );
}

type UpdateCheckState =
  | { phase: "checking" }
  | { phase: "ready"; result: DesktopUpdateCheckResult }
  | { phase: "error"; message: string };

const GITHUB_RELEASES_URL = "https://github.com/zxyao145/agw/releases";

function DesktopAboutSection() {
  const desktop = useDesktopRuntime();
  const [uninstallOpen, setUninstallOpen] = React.useState(false);
  const [deleteServerData, setDeleteServerData] = React.useState(false);
  const [updateState, setUpdateState] = React.useState<UpdateCheckState>({ phase: "checking" });
  const updateRequestId = React.useRef(0);
  const mounted = React.useRef(true);
  const didAutoCheck = React.useRef(false);

  const checkForUpdates = React.useCallback(async () => {
    const requestId = ++updateRequestId.current;
    setUpdateState({ phase: "checking" });
    try {
      const bridge = window.agwDesktop;
      if (!bridge) throw new Error("Desktop update checks require the Agw Desktop application.");
      const result = await bridge.checkForUpdates();
      if (mounted.current && updateRequestId.current === requestId) {
        setUpdateState({ phase: "ready", result });
      }
    } catch (error) {
      if (mounted.current && updateRequestId.current === requestId) {
        setUpdateState({
          phase: "error",
          message: error instanceof Error ? error.message : "Unable to check for updates.",
        });
      }
    }
  }, []);

  React.useEffect(() => {
    mounted.current = true;
    if (!didAutoCheck.current) {
      didAutoCheck.current = true;
      void checkForUpdates();
    }
    return () => {
      mounted.current = false;
    };
  }, [checkForUpdates]);

  const openExternal = async (url: string) => {
    try {
      const bridge = window.agwDesktop;
      if (!bridge) throw new Error("External links require the Agw Desktop application.");
      await bridge.openExternal(url);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to open the update link.");
    }
  };

  const runtimeState = desktop.runtimeState;
  const platformLabel =
    runtimeState?.platform === "darwin"
      ? "macOS"
      : runtimeState?.platform === "win32"
        ? "Windows"
        : runtimeState?.platform === "linux"
          ? "Linux"
          : (runtimeState?.platform ?? "Loading…");
  const editionLabel =
    runtimeState?.packageFlavor === "full"
      ? "Full"
      : runtimeState?.packageFlavor === "client"
        ? "Client"
        : "Loading…";
  const updateResult = updateState.phase === "ready" ? updateState.result : null;
  const updateDownloadUrl = updateResult?.downloadUrl ?? null;
  const UpdateIcon =
    updateState.phase === "checking"
      ? LoaderCircle
      : updateState.phase === "error"
        ? TriangleAlert
        : updateResult?.status === "available"
          ? Download
          : updateResult?.status === "ahead"
            ? ArrowUpRight
            : CheckCircle2;
  const updateTitle =
    updateState.phase === "checking"
      ? "Checking for updates…"
      : updateState.phase === "error"
        ? "Unable to check for updates"
        : updateResult?.status === "available"
          ? `Agw Desktop ${updateResult.latestVersion} is available`
          : updateResult?.status === "ahead"
            ? "This build is ahead of stable"
            : "Agw Desktop is up to date";
  const updateDescription =
    updateState.phase === "checking"
      ? "Looking for the latest stable release on GitHub."
      : updateState.phase === "error"
        ? updateState.message
        : updateResult?.status === "available" && !updateDownloadUrl
          ? `No ${editionLabel} installer is available for ${platformLabel} ${runtimeState?.architecture ?? ""}. Open the release to review the available downloads.`
          : updateResult?.status === "available"
            ? `Released ${formatLocalDateTime(updateResult.publishedAt)}. The matching ${editionLabel} package will download in your browser.`
            : updateResult?.status === "ahead"
              ? `This is newer than the latest stable release, ${updateResult.latestVersion}. No downgrade is offered.`
              : updateResult
                ? `${updateResult.latestVersion} is the latest stable release, published ${formatLocalDateTime(updateResult.publishedAt)}.`
                : "";

  return (
    <section id="about" className="scroll-mt-4 space-y-5">
      <div className="flex flex-col">
        <p className="text-xs font-semibold uppercase tracking-[0.12em] text-primary">About</p>
        <h1 className="mt-1 text-2xl font-semibold">About Agw Desktop</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Review this installation, check for updates, and manage removal.
        </p>
      </div>

      <Card className="gap-0 overflow-hidden py-0">
        <CardHeader className="relative overflow-hidden border-b py-6">
          <div className="pointer-events-none absolute inset-0 bg-gradient-to-br from-primary/[0.08] via-transparent to-transparent" />
          <div className="relative flex flex-wrap items-center justify-between gap-5">
            <div className="flex min-w-0 items-center gap-4">
              <span className="grid size-12 shrink-0 place-items-center rounded-2xl border bg-background/80 shadow-sm backdrop-blur">
                <AgwLogo showLabel={false} markClassName="size-8" />
              </span>
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2.5">
                  <CardTitle className="text-lg">Agw Desktop</CardTitle>
                  <Badge variant="secondary">
                    {runtimeState ? `v${runtimeState.appVersion}` : "Loading…"}
                  </Badge>
                </div>
                <CardDescription className="mt-1">
                  {runtimeState?.packageFlavor === "full"
                    ? "Desktop with the bundled Server daemon"
                    : runtimeState?.packageFlavor === "client"
                      ? "Desktop client for an existing Agw Server"
                      : "Loading package details…"}
                </CardDescription>
              </div>
            </div>
            <dl className="grid min-w-72 grid-cols-3 gap-x-6 gap-y-2 text-sm">
              <div>
                <dt className="text-xs text-muted-foreground">Edition</dt>
                <dd className="mt-0.5 font-medium">{editionLabel}</dd>
              </div>
              <div>
                <dt className="text-xs text-muted-foreground">Platform</dt>
                <dd className="mt-0.5 font-medium">
                  {platformLabel}
                  {runtimeState ? ` · ${runtimeState.architecture}` : ""}
                </dd>
              </div>
              <div>
                <dt className="text-xs text-muted-foreground">Server</dt>
                <dd className="mt-0.5 font-medium">
                  {desktop.serverInfo ? `v${desktop.serverInfo.serverVersion}` : "Unavailable"}
                </dd>
              </div>
            </dl>
          </div>
        </CardHeader>

        <CardContent className="py-5">
          <div
            className={cn(
              "flex flex-wrap items-center justify-between gap-4 rounded-xl border p-4",
              updateState.phase === "error" && "border-destructive/25 bg-destructive/[0.03]",
              updateResult?.status === "available" && "border-primary/25 bg-primary/[0.04]",
            )}
            aria-live="polite"
          >
            <div className="flex min-w-0 items-start gap-3">
              <span
                className={cn(
                  "grid size-9 shrink-0 place-items-center rounded-xl border bg-background text-muted-foreground",
                  updateState.phase === "error" && "text-destructive",
                  updateResult?.status === "available" && "text-primary",
                )}
              >
                <UpdateIcon
                  className={cn("size-4", updateState.phase === "checking" && "animate-spin")}
                />
              </span>
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <p className="text-sm font-medium">{updateTitle}</p>
                  {updateResult?.status === "available" ? (
                    <Badge>Update available</Badge>
                  ) : updateResult?.status === "ahead" ? (
                    <Badge variant="outline">Newer build</Badge>
                  ) : updateResult?.status === "up-to-date" ? (
                    <Badge variant="secondary">Latest stable</Badge>
                  ) : null}
                </div>
                <p className="mt-1 max-w-2xl text-xs leading-relaxed text-muted-foreground">
                  {updateDescription}
                </p>
                {updateResult?.assetName ? (
                  <p className="mt-1 truncate font-mono text-[11px] text-muted-foreground/80">
                    {updateResult.assetName}
                  </p>
                ) : null}
              </div>
            </div>

            <div className="flex shrink-0 flex-wrap items-center gap-2">
              {updateState.phase === "error" ? (
                <Button size="sm" onClick={() => void checkForUpdates()}>
                  <RefreshCw /> Retry
                </Button>
              ) : updateResult?.status === "available" && updateDownloadUrl ? (
                <Button size="sm" onClick={() => void openExternal(updateDownloadUrl)}>
                  <Download /> Download update
                </Button>
              ) : updateResult?.status === "available" ? (
                <Button size="sm" onClick={() => void openExternal(updateResult.releaseUrl)}>
                  <ExternalLink /> View release
                </Button>
              ) : null}

              {updateState.phase === "error" ? (
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => void openExternal(GITHUB_RELEASES_URL)}
                >
                  <ExternalLink /> GitHub Releases
                </Button>
              ) : updateResult?.status === "available" && updateDownloadUrl ? (
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => void openExternal(updateResult.releaseUrl)}
                >
                  Release notes <ExternalLink />
                </Button>
              ) : updateResult?.status === "ahead" ? (
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => void openExternal(updateResult.releaseUrl)}
                >
                  Stable release <ExternalLink />
                </Button>
              ) : null}

              {updateResult ? (
                <Button
                  size="icon-sm"
                  variant="outline"
                  aria-label="Check again"
                  title="Check again"
                  onClick={() => void checkForUpdates()}
                >
                  <RefreshCw />
                </Button>
              ) : null}
            </div>
          </div>
        </CardContent>

        <CardContent className="flex flex-wrap items-center justify-between gap-4 border-t py-5">
          <div>
            <p className="text-sm font-medium">Uninstall Agw Desktop</p>
            <p className="mt-1 text-xs text-muted-foreground">
              Unregister the bundled daemon and choose whether to keep Server data.
            </p>
          </div>
          <Button variant="destructive" onClick={() => setUninstallOpen(true)}>
            Uninstall…
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
                const result = await window.agwDesktop?.prepareUninstall({
                  deleteServerData,
                });
                if (result) toast.info(result.message);
                setUninstallOpen(false);
              }}
            >
              <HardDrive className="h-4 w-4" /> Prepare uninstall
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </section>
  );
}

export function DesktopSettingsPage() {
  return (
    <div className="w-full max-w-4xl space-y-12 py-6">
      <ServerProfilesPanel />
      <DesktopSettingsPanel />
      <DesktopAboutSection />
    </div>
  );
}
