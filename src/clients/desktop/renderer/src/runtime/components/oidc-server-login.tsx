"use client";

import * as React from "react";
import { LogIn, LogOut } from "lucide-react";
import { Button } from "@agw/components";
import { useDesktopRuntime } from "../runtime-provider";

export function OidcServerLogin({ profileId, baseUrl }: { profileId: string; baseUrl: string }) {
  const desktop = useDesktopRuntime();
  const [providers, setProviders] = React.useState<Array<{ id: string; displayName: string }>>([]);
  const [busy, setBusy] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const active = desktop.activeProfile?.id === profileId;
  const oidc = active && desktop.runtimeState?.activeCredentialSource === "oidc";

  React.useEffect(() => {
    let current = true;
    setProviders([]);
    setError(null);
    const bridge = window.agwDesktop;
    if (typeof bridge?.getOidcProviders !== "function") return;
    void bridge
      .getOidcProviders(profileId)
      .then((items) => {
        if (current) setProviders(items);
      })
      .catch(() => {
        if (current) setError("Third-party sign-in is unavailable for this Server.");
      });
    return () => {
      current = false;
    };
  }, [profileId, baseUrl]);

  const login = async (providerId: string) => {
    setBusy(providerId);
    setError(null);
    try {
      await window.agwDesktop!.loginWithOidc(profileId, providerId);
      await desktop.refresh();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Sign-in failed. Please start again.");
    } finally {
      setBusy(null);
    }
  };

  const logout = async () => {
    setBusy("logout");
    setError(null);
    try {
      await window.agwDesktop!.logoutOidc(profileId);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Unable to confirm Server sign-out.");
    } finally {
      await desktop.refresh();
      setBusy(null);
    }
  };

  if (providers.length === 0 && !error && !oidc) return null;
  return (
    <div className="space-y-2 sm:col-span-4">
      <div className="flex flex-wrap items-center gap-2">
        {providers.map((provider) => (
          <Button
            key={provider.id}
            type="button"
            variant="outline"
            size="sm"
            disabled={busy !== null}
            onClick={() => void login(provider.id)}
          >
            <LogIn className="mr-1.5 size-3.5" />
            {busy === provider.id ? "Waiting for browser…" : "Sign in with " + provider.displayName}
          </Button>
        ))}
        {busy && busy !== "logout" ? (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => void window.agwDesktop?.cancelOidcLogin(profileId)}
          >
            Cancel sign-in
          </Button>
        ) : null}
        {oidc && desktop.runtimeState?.activeToken ? (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={busy !== null}
            onClick={() => void logout()}
          >
            <LogOut className="mr-1.5 size-3.5" />
            Sign out
          </Button>
        ) : null}
        {oidc && desktop.activeProfile?.kind === "local" && !desktop.runtimeState?.activeToken ? (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            disabled={busy !== null}
            onClick={async () => {
              setBusy("local");
              try {
                await window.agwDesktop!.provisionLocalToken();
                await desktop.refresh();
              } catch {
                setError("Unable to connect as the local administrator.");
              } finally {
                setBusy(null);
              }
            }}
          >
            Use local administrator
          </Button>
        ) : null}
      </div>
      {busy && busy !== "logout" ? (
        <p className="text-xs text-muted-foreground" role="status">
          Complete sign-in in your system browser, then return to Agw.
        </p>
      ) : null}
      {error ? (
        <p className="text-xs text-destructive" role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
}
