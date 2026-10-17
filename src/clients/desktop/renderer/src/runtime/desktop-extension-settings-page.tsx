"use client";

import * as React from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { ArrowLeft } from "lucide-react";

import { Button } from "@agw/components";
import { ExtensionView } from "./components/workspace/extension-view";
import { settingsViewId } from "./extensions/extension-host";
import { useExtensionRuntime } from "./extensions/extension-host-provider";

/** 用 view frame 显示 extension 的设置页。Shows an extension's settings page in a view frame. */
export function DesktopExtensionSettingsPage() {
  const runtime = useExtensionRuntime();
  const extensionId = useSearchParams().get("id") ?? "";
  const snapshot = React.useSyncExternalStore(
    runtime?.host.subscribe ?? (() => () => undefined),
    () => runtime?.host.getSnapshot() ?? null,
    () => null,
  );
  const extension = snapshot?.extensions.find((item) => item.id === extensionId) ?? null;
  const manifest = extension?.installed.manifest ?? null;
  const entry = manifest?.contributes?.settings?.entry ?? null;

  return (
    <div className="flex h-full min-h-0 flex-col gap-3 p-6">
      <div className="flex items-center gap-2">
        <Button variant="ghost" size="sm" asChild>
          <Link href="/extensions/">
            <ArrowLeft /> Extensions
          </Link>
        </Button>
        <h1 className="text-lg font-semibold">{manifest?.name ?? extensionId} settings</h1>
      </div>
      {!runtime || !snapshot || !extension || !manifest || !entry ? (
        <p className="text-sm text-muted-foreground">This extension has no settings page.</p>
      ) : extension.status !== "active" ? (
        <p className="text-sm text-muted-foreground">
          Enable {manifest.name} to change its settings.
        </p>
      ) : (
        <div className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-xl border">
          <ExtensionView
            host={runtime.host}
            tracker={runtime.tracker}
            snapshot={snapshot}
            view={{
              id: settingsViewId(extension.id),
              location: "main",
              title: "Settings",
              icon: { kind: "lucide", name: "Settings" },
              extensionId: extension.id,
              extensionName: manifest.name,
              entry,
              retainContextWhenHidden: false,
              failure: null,
            }}
            openRequest={undefined}
          />
        </div>
      )}
    </div>
  );
}
