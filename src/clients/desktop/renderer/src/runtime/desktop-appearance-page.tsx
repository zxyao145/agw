"use client";

import { Card, CardContent } from "@agw/components";
import { AppearanceSettingsForm } from "./appearance/appearance-settings-form";
import { useDesktopRuntime } from "./runtime-provider";

export function DesktopAppearancePage() {
  const desktop = useDesktopRuntime();
  const settings = desktop.runtimeState?.settings;

  return (
    <div className="w-full max-w-4xl space-y-5 py-6">
      <div className="flex flex-col">
        <p className="text-xs font-semibold uppercase tracking-[0.12em] text-primary">Appearance</p>
        <h1 className="mt-1 text-2xl font-semibold">Font sizes</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Choose the text sizes used across Desktop, Chat and documents.
        </p>
      </div>
      {settings ? (
        <AppearanceSettingsForm
          appearance={settings.appearance}
          save={async (appearance) => {
            await desktop.saveSettings({ appearance });
          }}
        />
      ) : (
        <Card>
          <CardContent className="py-6 text-sm text-muted-foreground">
            Loading Desktop settings…
          </CardContent>
        </Card>
      )}
    </div>
  );
}
