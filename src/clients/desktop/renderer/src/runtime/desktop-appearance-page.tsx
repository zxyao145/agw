"use client";

import { useSearchParams } from "next/navigation";

import { Card, CardContent } from "@agw/components";
import { AppearanceSettingsForm } from "./appearance/appearance-settings-form";
import { useDesktopTheme } from "./appearance/theme-provider";
import { buildSettingsHref, getDesktopChatReturnHref } from "./components/app-shell-routing";

export function DesktopAppearancePage() {
  const { appearance, themes, saveAppearance } = useDesktopTheme();
  const returnTo = useSearchParams().get("returnTo");

  return (
    <div className="w-full max-w-4xl space-y-5 py-6">
      <div className="flex flex-col">
        <p className="text-xs font-semibold uppercase tracking-[0.12em] text-primary">Appearance</p>
        <h1 className="mt-1 text-2xl font-semibold">Theme and font sizes</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Choose the mode, theme and text sizes used across Desktop, Chat and documents.
        </p>
      </div>
      {appearance ? (
        <AppearanceSettingsForm
          appearance={appearance}
          themes={themes}
          manageThemesHref={buildSettingsHref(
            "/extensions/?category=themes",
            getDesktopChatReturnHref(returnTo),
          )}
          save={saveAppearance}
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
