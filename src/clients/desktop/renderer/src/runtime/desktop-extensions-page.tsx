"use client";

import * as React from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import {
  AlertTriangle,
  ChevronDown,
  FolderOpen,
  FolderPlus,
  PackagePlus,
  RefreshCw,
  RotateCw,
  Save,
  Settings,
  Trash2,
} from "lucide-react";
import { toast } from "sonner";

import {
  Badge,
  Button,
  Checkbox,
  cn,
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Label,
  Switch,
} from "@agw/components";
import type { ExtensionInstallPreview, ExtensionRecoveryNotice } from "@desktop/shared/contracts";
import { type ExtensionManifest, HIGH_RISK_EXTENSION_PERMISSIONS } from "@agw/extension-sdk";
import {
  isUnencryptedHostPermission,
  parseHostPermission,
} from "@desktop/shared/extension-host-permissions";
import {
  PERMISSION_DESCRIPTIONS,
  READ_PERMISSIONS,
} from "@desktop/shared/extension-permission-descriptions";
import { buildSettingsHref, getDesktopChatReturnHref } from "./components/app-shell-routing";
import {
  EXTENSION_CATEGORIES,
  extensionsInCategory,
  themeTitles,
} from "./extensions/extension-categories";
import { extensionIcon } from "./extensions/extension-contributions";
import type { ExtensionHost, ExtensionState } from "./extensions/extension-host";
import { useExtensionRuntime } from "./extensions/extension-host-provider";
import { WorkspaceIconView } from "./components/workspace/workspace-icon";

const STATUS_LABELS: Record<ExtensionState["status"], string> = {
  inactive: "Disabled",
  activating: "Activating",
  active: "Active",
  deactivating: "Deactivating",
  failed: "Failed",
  invalid: "Invalid",
  incompatible: "Incompatible",
};

function useHostSnapshot(host: ExtensionHost) {
  return React.useSyncExternalStore(host.subscribe, host.getSnapshot, host.getSnapshot);
}

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function run(task: () => Promise<void>): void {
  void task().catch((error: unknown) => toast.error(describeError(error)));
}

function recoveryMessage(notice: ExtensionRecoveryNotice): string {
  if (notice.operation === "uninstall")
    return `The unfinished uninstall of ${notice.extensionId} was completed.`;
  return notice.outcome === "restored-previous"
    ? `${notice.extensionId} was restored to its previous version after an interrupted install.`
    : `The interrupted install of ${notice.extensionId} was completed.`;
}

function PermissionList({ manifest }: { manifest: ExtensionManifest }) {
  const permissions = manifest.permissions ?? [];
  const hosts = manifest.hostPermissions ?? [];
  const readsData = permissions.some((permission) => READ_PERMISSIONS.includes(permission));
  return (
    <div className="space-y-3 text-sm">
      {permissions.length === 0 && hosts.length === 0 ? (
        <p className="text-muted-foreground">This extension requests no permissions.</p>
      ) : null}
      {permissions.length > 0 ? (
        <ul className="space-y-1.5">
          {permissions.map((permission) => (
            <li
              key={permission}
              className={cn(
                HIGH_RISK_EXTENSION_PERMISSIONS.has(permission) &&
                  "text-amber-600 dark:text-amber-400",
              )}
            >
              <span className="font-mono text-xs">{permission}</span> —{" "}
              {PERMISSION_DESCRIPTIONS[permission]}
            </li>
          ))}
        </ul>
      ) : null}
      {hosts.length > 0 ? (
        <div>
          <p className="font-medium">Network access</p>
          <ul className="mt-1 space-y-1">
            {hosts.map((host) => {
              const parsed = parseHostPermission(host);
              return (
                <li key={host} className="font-mono text-xs">
                  {host}
                  {parsed && isUnencryptedHostPermission(parsed) ? (
                    <span className="ml-2 font-sans text-amber-600 dark:text-amber-400">
                      Unencrypted connection
                    </span>
                  ) : null}
                </li>
              );
            })}
          </ul>
        </div>
      ) : null}
      {readsData && hosts.length > 0 ? (
        <p className="flex gap-2 text-amber-600 dark:text-amber-400">
          <AlertTriangle className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
          This extension can send the data it reads to the hosts listed above.
        </p>
      ) : null}
      {permissions.includes("execution") ? (
        <p className="text-muted-foreground">
          This extension can run your Agents and answer tool approvals on your behalf.
        </p>
      ) : null}
    </div>
  );
}

/**
 * 不能安装的包：显示校验错误。可以安装的包由主进程的确认窗口批准，不在这里显示。
 * A package that cannot be installed shows its validation errors. Installable packages are approved in the main
 * process's confirmation window instead.
 */
function InstallErrorsDialog({
  preview,
  onClose,
}: {
  preview: Extract<ExtensionInstallPreview, { canceled: false }> | null;
  onClose(): void;
}) {
  const manifest = preview?.manifest ?? null;
  return (
    <Dialog open={preview !== null} onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent size="md">
        <DialogHeader>
          <DialogTitle>{manifest ? `Install ${manifest.name}` : "Install extension"}</DialogTitle>
          <DialogDescription>
            {manifest
              ? `Version ${manifest.version} cannot be installed.`
              : "The package could not be read."}
          </DialogDescription>
        </DialogHeader>
        <ul className="space-y-1 text-sm text-destructive">
          {preview?.errors.map((error, index) => (
            <li key={index}>
              {error.code}
              {error.path ? ` (${error.path})` : ""}: {error.message}
            </li>
          ))}
        </ul>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Close
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function UninstallDialog({
  extension,
  onCancel,
  onConfirm,
}: {
  extension: ExtensionState | null;
  onCancel(): void;
  onConfirm(deleteData: boolean): void;
}) {
  const [deleteData, setDeleteData] = React.useState(false);
  React.useEffect(() => setDeleteData(false), [extension]);
  return (
    <Dialog open={extension !== null} onOpenChange={(open) => (open ? undefined : onCancel())}>
      <DialogContent size="sm">
        <DialogHeader>
          <DialogTitle>
            Uninstall {extension?.installed.manifest?.name ?? extension?.id}?
          </DialogTitle>
          <DialogDescription>The extension is removed from Agw Desktop.</DialogDescription>
        </DialogHeader>
        <div className="flex items-center gap-2">
          <Checkbox
            id="extension-delete-data"
            checked={deleteData}
            onCheckedChange={(checked) => setDeleteData(checked === true)}
          />
          <Label htmlFor="extension-delete-data">Also delete extension data</Label>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onCancel}>
            Cancel
          </Button>
          <Button variant="destructive" onClick={() => onConfirm(deleteData)}>
            Uninstall
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function ExtensionCard({
  host,
  extension,
  onEnable,
  onUninstall,
}: {
  host: ExtensionHost;
  extension: ExtensionState;
  onEnable(extension: ExtensionState): void;
  onUninstall(extension: ExtensionState): void;
}) {
  const { installed } = extension;
  const manifest = installed.manifest;
  const bridge = window.agwDesktop!;
  const returnTo = useSearchParams().get("returnTo");
  // 激活期间可以停用（立即完成）；停用期间等待完成。Disabling is allowed while activating; wait while deactivating.
  const busy = extension.status === "deactivating";
  const canEnable = installed.validation === "valid";
  const themes = themeTitles(manifest);
  return (
    <section className="rounded-xl border bg-card p-4" data-extension-id={extension.id}>
      <div className="flex items-start gap-3">
        <div className="grid size-9 shrink-0 place-items-center rounded-lg border">
          <WorkspaceIconView icon={extensionIcon(installed)} className="size-5" />
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="font-medium">{manifest?.name ?? extension.id}</h2>
            {manifest ? (
              <span className="text-xs text-muted-foreground">{manifest.version}</span>
            ) : null}
            <Badge
              variant={extension.status === "failed" || !canEnable ? "destructive" : "secondary"}
            >
              {STATUS_LABELS[extension.status]}
            </Badge>
            {installed.permissionApprovalRequired ? (
              <Badge variant="outline">Permission approval required</Badge>
            ) : null}
            {themes.length > 0 ? <Badge variant="outline">Theme</Badge> : null}
          </div>
          <p className="mt-0.5 text-xs text-muted-foreground">
            {extension.id}
            {manifest?.author ? ` · ${manifest.author}` : ""}
            {manifest ? ` · Extension API ${manifest.manifestVersion}` : ""}
          </p>
          {manifest?.description ? <p className="mt-2 text-sm">{manifest.description}</p> : null}
          {themes.length > 0 ? (
            <p className="mt-2 text-sm" data-testid="extension-themes">
              Themes: {themes.join(", ")}
            </p>
          ) : null}
          {extension.error ? (
            <p className="mt-2 text-sm text-destructive">
              {extension.error.code}: {extension.error.message}
            </p>
          ) : null}
          {installed.errors.length > 0 ? (
            <ul className="mt-2 space-y-0.5 text-sm text-destructive">
              {installed.errors.map((error, index) => (
                <li key={index}>
                  {error.code}
                  {error.path ? ` (${error.path})` : ""}: {error.message}
                </li>
              ))}
            </ul>
          ) : null}
        </div>
        <Switch
          aria-label={`Enable ${manifest?.name ?? extension.id}`}
          checked={installed.enabled && canEnable}
          disabled={!canEnable || busy}
          onCheckedChange={(checked) => {
            if (checked) onEnable(extension);
            else run(() => host.disable(extension.id));
          }}
        />
      </div>
      <div className="mt-3 flex flex-wrap gap-2">
        <Button
          size="sm"
          variant="outline"
          disabled={!canEnable}
          onClick={() => run(() => host.reload(extension.id))}
        >
          <RotateCw /> Reload
        </Button>
        {manifest?.contributes?.settings ? (
          <Button size="sm" variant="outline" asChild>
            <Link
              href={buildSettingsHref(
                `/extensions/settings/?id=${encodeURIComponent(extension.id)}`,
                getDesktopChatReturnHref(returnTo),
              )}
            >
              <Settings /> Settings
            </Link>
          </Button>
        ) : null}
        <Button
          size="sm"
          variant="outline"
          onClick={() => run(() => bridge.openExtensionDataDirectory(extension.id))}
        >
          <FolderOpen /> Open data folder
        </Button>
        <Button size="sm" variant="outline" onClick={() => onUninstall(extension)}>
          <Trash2 /> Uninstall
        </Button>
      </div>
      {manifest ? (
        <Collapsible className="mt-3">
          <CollapsibleTrigger className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground">
            <ChevronDown className="size-3.5" /> Details
          </CollapsibleTrigger>
          <CollapsibleContent className="mt-2 space-y-3">
            <PermissionList manifest={manifest} />
            {extension.frames.length > 0 ? (
              <div className="text-sm">
                <p className="font-medium">Frames</p>
                <ul className="mt-1 space-y-1">
                  {extension.frames.map((frame) => (
                    <li key={frame.frameId} className="flex items-center gap-2">
                      <span className="font-mono text-xs">
                        {frame.kind}
                        {frame.viewId ? ` · ${frame.viewId}` : ""}
                      </span>
                      <Button
                        size="sm"
                        variant="ghost"
                        onClick={() => host.openDevTools(frame.frameId)}
                      >
                        Inspect
                      </Button>
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}
            {extension.blockedRequests.length > 0 ? (
              <div className="text-sm">
                <p className="font-medium">Recently blocked requests</p>
                <ul
                  className="mt-1 space-y-0.5 font-mono text-xs break-all"
                  data-testid="blocked-requests"
                >
                  {extension.blockedRequests.map((request, index) => (
                    <li key={index}>
                      {request.reason} · {request.resourceType} · {request.url}
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}
          </CollapsibleContent>
        </Collapsible>
      ) : null}
    </section>
  );
}

function ExtensionsPageContent({ host }: { host: ExtensionHost }) {
  const snapshot = useHostSnapshot(host);
  const bridge = window.agwDesktop!;
  const searchParams = useSearchParams();
  const categoryName = searchParams.get("category");
  const extensions = extensionsInCategory(snapshot.extensions, categoryName);
  const showAllHref = buildSettingsHref(
    "/extensions/",
    getDesktopChatReturnHref(searchParams.get("returnTo")),
  );
  const [uninstalling, setUninstalling] = React.useState<ExtensionState | null>(null);
  const [invalidPreview, setInvalidPreview] = React.useState<Extract<
    ExtensionInstallPreview,
    { canceled: false }
  > | null>(null);

  // 可以安装的包由主进程的确认窗口批准，批准之后才停用已安装的版本并提交。
  // The main process's confirmation window approves installable packages; only then is the installed version
  // deactivated and the install committed.
  const install = (source: "directory" | "archive") =>
    run(async () => {
      const preview = await bridge.prepareExtensionInstall({ source });
      if (preview.canceled) return;
      if (preview.errors.length > 0 || !preview.manifest) {
        setInvalidPreview(preview);
        return;
      }
      if (!(await bridge.approveExtensionInstall(preview.installId))) {
        await bridge.cancelExtensionInstall(preview.installId);
        return;
      }
      await host.prepareReplace(preview.manifest.id);
      const installed = await bridge.commitExtensionInstall(preview.installId);
      await host.afterInstall(installed.id);
      toast.success(`${installed.manifest?.name ?? installed.id} installed.`);
    });

  return (
    <div className="mx-auto w-full max-w-4xl space-y-4 p-6">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold">Extensions</h1>
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="outline" onClick={() => install("directory")}>
            <FolderPlus /> Install from folder…
          </Button>
          <Button size="sm" variant="outline" onClick={() => install("archive")}>
            <PackagePlus /> Install from .zip…
          </Button>
          <Button size="sm" variant="outline" onClick={() => run(() => host.rescan())}>
            <RefreshCw /> Rescan
          </Button>
          <Button
            size="sm"
            variant="outline"
            onClick={() => run(() => bridge.openExtensionsDirectory())}
          >
            <FolderOpen /> Open extensions folder
          </Button>
          <Button
            size="sm"
            variant="outline"
            onClick={() =>
              run(async () => {
                const result = await bridge.saveExtensionSdkTypes();
                if (!result.canceled) toast.success(`SDK types saved to ${result.directory}`);
              })
            }
          >
            <Save /> Save SDK types…
          </Button>
        </div>
      </header>

      {snapshot.safeMode ? (
        <div
          role="status"
          className="rounded-xl border border-amber-500/40 bg-amber-500/10 p-4 text-sm"
          data-testid="safe-mode"
        >
          <p className="font-medium">Extensions are paused (safe mode).</p>
          <p className="mt-1 text-muted-foreground">
            {snapshot.safeMode.reason === "manual"
              ? "Agw Desktop was reloaded without extensions."
              : `Agw Desktop ${snapshot.safeMode.reason === "crashed" ? "crashed" : "stopped responding"} while extensions were activating.`}
            {snapshot.safeMode.extensionIds.length > 0
              ? ` Involved: ${snapshot.safeMode.extensionIds.join(", ")}.`
              : ""}
          </p>
          <Button size="sm" className="mt-3" onClick={() => run(() => host.resume())}>
            Resume extensions
          </Button>
        </div>
      ) : null}

      {snapshot.recoveryNotices.map((notice) => (
        <p
          key={`${notice.extensionId}:${notice.operation}`}
          className="rounded-xl border p-3 text-sm"
        >
          {recoveryMessage(notice)}
        </p>
      ))}

      {categoryName !== null ? (
        <div className="flex items-center gap-3 text-sm" data-testid="extension-category">
          <span className="font-medium">
            {extensions
              ? EXTENSION_CATEGORIES[categoryName].label
              : `Unknown category: ${categoryName}`}
          </span>
          <Button size="sm" variant="outline" asChild>
            <Link href={showAllHref}>Show all</Link>
          </Button>
        </div>
      ) : null}

      {extensions ? (
        <>
          {extensions.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              {categoryName === null
                ? "No extensions are installed."
                : `No ${EXTENSION_CATEGORIES[categoryName].label.toLowerCase()} are installed.`}
            </p>
          ) : null}
          {extensions.map((extension) => (
            <ExtensionCard
              key={extension.id}
              host={host}
              extension={extension}
              onEnable={(item) => run(async () => void (await host.enable(item.id)))}
              onUninstall={setUninstalling}
            />
          ))}
        </>
      ) : null}

      <UninstallDialog
        extension={uninstalling}
        onCancel={() => setUninstalling(null)}
        onConfirm={(deleteData) => {
          const extension = uninstalling!;
          setUninstalling(null);
          run(() => host.uninstall(extension.id, deleteData));
        }}
      />
      <InstallErrorsDialog
        preview={invalidPreview}
        onClose={() => {
          const current = invalidPreview;
          setInvalidPreview(null);
          if (current) run(() => bridge.cancelExtensionInstall(current.installId));
        }}
      />
    </div>
  );
}

export function DesktopExtensionsPage() {
  const runtime = useExtensionRuntime();
  if (!runtime || typeof window === "undefined" || !window.agwDesktop) {
    return (
      <p className="p-6 text-sm text-muted-foreground">Extensions are available in Agw Desktop.</p>
    );
  }
  return <ExtensionsPageContent host={runtime.host} />;
}
