"use client";

import * as React from "react";
import { LockKeyhole } from "lucide-react";

import { getOidcProviders, oidcLoginUrl, type OidcProvider, login } from "../../services/auth";
import { AgwLogo, Button } from "@agw/components";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@agw/components";
import { Input } from "@agw/components";
import { Label } from "@agw/components";

export default function LoginPage() {
  const [providers, setProviders] = React.useState<OidcProvider[]>([]);
  const [loadingProviders, setLoadingProviders] = React.useState(true);
  const [providerError, setProviderError] = React.useState(false);
  const [password, setPassword] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [submitting, setSubmitting] = React.useState(false);

  React.useEffect(() => {
    let active = true;
    getOidcProviders()
      .then((items) => {
        if (active) setProviders(items);
      })
      .catch(() => {
        if (active) setProviderError(true);
      })
      .finally(() => {
        if (active) setLoadingProviders(false);
      });
    return () => {
      active = false;
    };
  }, []);

  React.useEffect(() => {
    const loginError = new URLSearchParams(window.location.search).get("error");
    if (loginError?.startsWith("oidc-")) {
      setError(
        loginError === "oidc-authorization-denied"
          ? "Sign-in was cancelled. Choose a provider to try again."
          : "Sign-in could not be completed. Please start again.",
      );
    }
    if (new URLSearchParams(window.location.search).get("error") === "incompatible-server") {
      setError("This Web UI is not compatible with the connected Agw Server API version.");
    }
  }, []);

  const handleSubmit = async (event: React.FormEvent) => {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      await login(password);
      const returnUrl = new URLSearchParams(window.location.search).get("returnUrl");
      const destination =
        returnUrl && returnUrl.startsWith("/") && !returnUrl.startsWith("//")
          ? returnUrl
          : "/dashboard/";
      window.location.assign(destination);
    } catch {
      setError("The administrator password was not accepted.");
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <main className="relative grid min-h-screen place-items-center overflow-hidden bg-background px-6 py-12">
      <div className="pointer-events-none absolute inset-0 bg-[linear-gradient(to_right,color-mix(in_oklab,var(--border)_45%,transparent)_1px,transparent_1px),linear-gradient(to_bottom,color-mix(in_oklab,var(--border)_45%,transparent)_1px,transparent_1px)] bg-[size:40px_40px] [mask-image:radial-gradient(circle_at_center,black,transparent_75%)]" />
      <Card className="relative w-full max-w-md border-border/80 shadow-xl shadow-black/5">
        <CardHeader>
          <div className="flex justify-between items-center">
            <AgwLogo
              markClassName="size-11"
              labelClassName="text-base font-semibold tracking-tight"
            />
            <CardTitle className="text-2xl">Connect to Agw Server</CardTitle>
          </div>
          <CardDescription>
            Authenticate this browser to manage agents, projects and executions.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="space-y-3" aria-busy={loadingProviders}>
            {loadingProviders ? (
              <p className="text-sm text-muted-foreground">Loading sign-in options…</p>
            ) : null}
            {providerError ? (
              <p className="text-sm text-muted-foreground" role="status">
                Sign-in providers are unavailable. You can still use the administrator password.
              </p>
            ) : null}
            {providers.map((provider) => (
              <Button
                key={provider.id}
                className="w-full"
                variant="outline"
                disabled={submitting}
                onClick={() => {
                  setSubmitting(true);
                  const requested = new URLSearchParams(window.location.search).get("returnUrl");
                  const returnUrl =
                    requested?.startsWith("/") && !requested.startsWith("//")
                      ? requested
                      : "/dashboard/";
                  window.location.assign(oidcLoginUrl(provider.id, returnUrl));
                }}
              >
                Continue with {provider.displayName}
              </Button>
            ))}
            {providers.length > 0 ? (
              <div className="py-2 text-center text-xs text-muted-foreground">
                Administrator access
              </div>
            ) : null}
          </div>
          <form className="space-y-4" onSubmit={handleSubmit}>
            <div className="space-y-2">
              <Label htmlFor="password">Administrator password</Label>
              <div className="relative">
                <LockKeyhole className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                <Input
                  id="password"
                  type="password"
                  autoComplete="current-password"
                  className="pl-9"
                  value={password}
                  onChange={(event) => setPassword(event.target.value)}
                  required
                />
              </div>
            </div>
            {error ? <p className="text-sm text-destructive">{error}</p> : null}
            <Button className="w-full" type="submit" disabled={submitting || password.length === 0}>
              {submitting ? "Connecting…" : "Continue"}
            </Button>
          </form>
        </CardContent>
      </Card>
    </main>
  );
}
