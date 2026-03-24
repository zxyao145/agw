"use client";

import * as React from "react";
import {
  CheckCircle2,
  Copy,
  ExternalLink,
  KeyRound,
  Link2,
  RefreshCw,
  ShieldCheck,
} from "lucide-react";
import { toast } from "sonner";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardFooter,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";

type IntegrationTemplate = {
  id: string;
  name: string;
  description: string;
  provider: string;
  status: "ready" | "connected" | "needs-review";
  authUrl: string;
  clientId: string;
  scopes: string[];
  redirectPath: string;
  uiCallbackPath: string;
};

type OAuthFormState = {
  authUrl: string;
  clientId: string;
  scopes: string;
  redirectUri: string;
  usePkce: boolean;
};

const integrationTemplates: IntegrationTemplate[] = [
  {
    id: "github",
    name: "GitHub",
    description:
      "Authorize repository access so agents can read issues, pull requests, and deployment metadata.",
    provider: "GitHub OAuth App",
    status: "ready",
    authUrl: "https://github.com/login/oauth/authorize",
    clientId: "agw-github-client",
    scopes: ["repo", "read:user", "read:org"],
    redirectPath: "/api/integrations/oauth/callback",
    uiCallbackPath: "/integrations/callback",
  },
  {
    id: "slack",
    name: "Slack",
    description:
      "Grant workspace permissions to post updates, inspect channels, and route task notifications.",
    provider: "Slack OAuth v2",
    status: "connected",
    authUrl: "https://slack.com/oauth/v2/authorize",
    clientId: "agw-slack-client",
    scopes: ["channels:read", "chat:write", "users:read"],
    redirectPath: "/api/integrations/oauth/callback",
    uiCallbackPath: "/integrations/callback",
  },
  {
    id: "google-workspace",
    name: "Google Workspace",
    description:
      "Connect calendars, files, and docs so workflows can act on shared organizational context.",
    provider: "Google OAuth 2.0",
    status: "needs-review",
    authUrl: "https://accounts.google.com/o/oauth2/v2/auth",
    clientId: "agw-google-client",
    scopes: [
      "openid",
      "email",
      "https://www.googleapis.com/auth/drive.readonly",
      "https://www.googleapis.com/auth/calendar.readonly",
    ],
    redirectPath: "/api/integrations/oauth/callback",
    uiCallbackPath: "/integrations/callback",
  },
];

const statusBadgeVariant: Record<
  IntegrationTemplate["status"],
  "default" | "secondary" | "outline"
> = {
  ready: "secondary",
  connected: "default",
  "needs-review": "outline",
};

const statusLabel: Record<IntegrationTemplate["status"], string> = {
  ready: "Ready",
  connected: "Connected",
  "needs-review": "Review scopes",
};

function buildRedirectUri(path: string) {
  if (typeof window === "undefined") {
    return path;
  }

  return new URL(path, window.location.origin).toString();
}

function encodeBase64Url(value: ArrayBuffer | Uint8Array | string) {
  const bytes =
    typeof value === "string"
      ? new TextEncoder().encode(value)
      : value instanceof Uint8Array
        ? value
        : new Uint8Array(value);
  let binary = "";

  bytes.forEach((byte) => {
    binary += String.fromCharCode(byte);
  });

  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

async function createPkcePair() {
  const verifier = encodeBase64Url(crypto.getRandomValues(new Uint8Array(32)));
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));

  return {
    verifier,
    challenge: encodeBase64Url(digest),
  };
}

function buildUiCallbackUri(path: string) {
  if (typeof window === "undefined") {
    return path;
  }

  return new URL(path, window.location.origin).toString();
}

function createInitialFormState(template: IntegrationTemplate): OAuthFormState {
  return {
    authUrl: template.authUrl,
    clientId: template.clientId,
    scopes: template.scopes.join("\n"),
    redirectUri: template.redirectPath,
    usePkce: true,
  };
}

export default function IntegrationsPage() {
  const [selectedIntegrationId, setSelectedIntegrationId] = React.useState(
    integrationTemplates[0]?.id ?? "",
  );
  const [isAuthorizing, setIsAuthorizing] = React.useState(false);
  const selectedIntegration = React.useMemo(
    () =>
      integrationTemplates.find((template) => template.id === selectedIntegrationId) ??
      integrationTemplates[0],
    [selectedIntegrationId],
  );
  const [form, setForm] = React.useState<OAuthFormState>(() =>
    createInitialFormState(selectedIntegration),
  );

  React.useEffect(() => {
    setForm(createInitialFormState(selectedIntegration));
  }, [selectedIntegration]);

  const resolvedRedirectUri = React.useMemo(
    () => buildRedirectUri(form.redirectUri),
    [form.redirectUri],
  );
  const resolvedUiCallbackUri = React.useMemo(
    () => buildUiCallbackUri(selectedIntegration.uiCallbackPath),
    [selectedIntegration.uiCallbackPath],
  );

  const scopeList = React.useMemo(
    () =>
      form.scopes
        .split(/\r?\n|\s+/)
        .map((scope) => scope.trim())
        .filter(Boolean),
    [form.scopes],
  );

  const handleCopyRedirectUri = async () => {
    try {
      await navigator.clipboard.writeText(resolvedRedirectUri);
      toast.success("Callback URL copied");
    } catch {
      toast.error("Unable to copy callback URL");
    }
  };

  const handleAuthorize = async () => {
    if (!form.authUrl.trim() || !form.clientId.trim() || scopeList.length === 0) {
      toast.error("Authorization URL, client ID, and at least one scope are required.");
      return;
    }

    setIsAuthorizing(true);

    try {
      const state = crypto.randomUUID();
      const url = new URL(form.authUrl.trim());
      const params = new URLSearchParams({
        response_type: "code",
        client_id: form.clientId.trim(),
        redirect_uri: resolvedRedirectUri,
        scope: scopeList.join(" "),
        state,
      });

      if (form.usePkce) {
        const pkce = await createPkcePair();
        sessionStorage.setItem(
          `agw.oauth2.${selectedIntegration.id}`,
          JSON.stringify({
            state,
            verifier: pkce.verifier,
            integrationId: selectedIntegration.id,
            createdAt: new Date().toISOString(),
          }),
        );
        params.set("code_challenge", pkce.challenge);
        params.set("code_challenge_method", "S256");
      } else {
        sessionStorage.setItem(
          `agw.oauth2.${selectedIntegration.id}`,
          JSON.stringify({
            state,
            integrationId: selectedIntegration.id,
            createdAt: new Date().toISOString(),
          }),
        );
      }

      url.search = params.toString();
      window.open(url.toString(), "_self");
    } catch (error) {
      const message = error instanceof Error ? error.message : "Unable to start OAuth2 login.";
      toast.error(message);
      setIsAuthorizing(false);
    }
  };

  return (
    <div className="w-full max-w-7xl space-y-6 py-4">
      <div className="space-y-2">
        <h1 className="text-3xl font-semibold tracking-tight">Integrations</h1>
        <p className="text-sm text-muted-foreground">
          Start OAuth2 flows for external apps, review requested scopes, and send provider callbacks
          through Agw's backend before returning users to the integrations UI.
        </p>
      </div>

      <div className="grid gap-6 xl:grid-cols-[1.1fr_0.9fr]">
        <Card>
          <CardHeader>
            <CardTitle>Available integrations</CardTitle>
            <CardDescription>
              Choose an app connection to prefill its OAuth2 authorization settings.
            </CardDescription>
          </CardHeader>
          <CardContent className="grid gap-4 md:grid-cols-2 xl:grid-cols-1">
            {integrationTemplates.map((integration) => {
              const isSelected = integration.id === selectedIntegration.id;

              return (
                <button
                  key={integration.id}
                  type="button"
                  onClick={() => setSelectedIntegrationId(integration.id)}
                  className={`rounded-xl border p-4 text-left transition hover:border-primary hover:shadow-sm ${
                    isSelected ? "border-primary bg-primary/5" : "border-border"
                  }`}
                >
                  <div className="flex items-start justify-between gap-3">
                    <div>
                      <div className="flex items-center gap-2">
                        <h2 className="text-base font-medium">{integration.name}</h2>
                        <Badge variant={statusBadgeVariant[integration.status]}>
                          {statusLabel[integration.status]}
                        </Badge>
                      </div>
                      <p className="mt-1 text-sm text-muted-foreground">{integration.provider}</p>
                    </div>
                    <Link2 className="mt-0.5 size-4 text-muted-foreground" />
                  </div>
                  <p className="mt-3 text-sm leading-6 text-muted-foreground">
                    {integration.description}
                  </p>
                  <div className="mt-4 flex flex-wrap gap-2">
                    {integration.scopes.map((scope) => (
                      <Badge key={scope} variant="outline">
                        {scope}
                      </Badge>
                    ))}
                  </div>
                </button>
              );
            })}
          </CardContent>
        </Card>

        <div className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>OAuth2 launchpad</CardTitle>
              <CardDescription>
                Configure the authorization request that sends users to {selectedIntegration.name}.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-5">
              <div className="grid gap-2">
                <Label htmlFor="auth-url">Authorization URL</Label>
                <Input
                  id="auth-url"
                  value={form.authUrl}
                  onChange={(event) =>
                    setForm((current) => ({ ...current, authUrl: event.target.value }))
                  }
                  placeholder="https://provider.example.com/oauth/authorize"
                />
              </div>

              <div className="grid gap-2 md:grid-cols-2">
                <div className="grid gap-2">
                  <Label htmlFor="client-id">Client ID</Label>
                  <Input
                    id="client-id"
                    value={form.clientId}
                    onChange={(event) =>
                      setForm((current) => ({ ...current, clientId: event.target.value }))
                    }
                    placeholder="agw-client-id"
                  />
                </div>
                <div className="grid gap-2">
                  <Label htmlFor="redirect-uri">Redirect URI</Label>
                  <Input
                    id="redirect-uri"
                    value={form.redirectUri}
                    onChange={(event) =>
                      setForm((current) => ({
                        ...current,
                        redirectUri: event.target.value,
                      }))
                    }
                    placeholder="/integrations/callback"
                  />
                  <p className="text-xs text-muted-foreground">
                    Backend callback: {resolvedRedirectUri}
                  </p>
                  <p className="text-xs text-muted-foreground">
                    UI hand-off: {resolvedUiCallbackUri}
                  </p>
                </div>
              </div>

              <div className="grid gap-2">
                <Label htmlFor="scopes">Scopes</Label>
                <Textarea
                  id="scopes"
                  value={form.scopes}
                  onChange={(event) =>
                    setForm((current) => ({ ...current, scopes: event.target.value }))
                  }
                  rows={6}
                  placeholder="openid\nprofile\nemail"
                />
                <p className="text-xs text-muted-foreground">
                  Enter one scope per line. The launch button will send them as a space-delimited
                  OAuth2 scope parameter.
                </p>
              </div>

              <div className="flex items-center justify-between rounded-lg border px-4 py-3">
                <div>
                  <Label htmlFor="pkce" className="text-sm font-medium">
                    Use PKCE
                  </Label>
                  <p className="text-xs text-muted-foreground">
                    Recommended for public clients and browser-based authorization flows.
                  </p>
                </div>
                <Switch
                  id="pkce"
                  checked={form.usePkce}
                  onCheckedChange={(checked) =>
                    setForm((current) => ({ ...current, usePkce: checked }))
                  }
                />
              </div>
            </CardContent>
            <CardFooter className="flex flex-wrap justify-between gap-3 border-t pt-6">
              <Button type="button" variant="outline" onClick={handleCopyRedirectUri}>
                <Copy className="mr-2 size-4" />
                Copy callback URL
              </Button>
              <Button type="button" onClick={() => void handleAuthorize()} disabled={isAuthorizing}>
                {isAuthorizing ? (
                  <RefreshCw className="mr-2 size-4 animate-spin" />
                ) : (
                  <ExternalLink className="mr-2 size-4" />
                )}
                Connect with OAuth2
              </Button>
            </CardFooter>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>What this page enables</CardTitle>
              <CardDescription>
                A compact checklist for admins reviewing third-party access before users consent.
              </CardDescription>
            </CardHeader>
            <CardContent className="grid gap-4 sm:grid-cols-3">
              <div className="rounded-lg border p-4">
                <ShieldCheck className="size-5 text-primary" />
                <h3 className="mt-3 font-medium">Consent review</h3>
                <p className="mt-2 text-sm text-muted-foreground">
                  Inspect requested scopes before redirecting users to an external provider.
                </p>
              </div>
              <div className="rounded-lg border p-4">
                <KeyRound className="size-5 text-primary" />
                <h3 className="mt-3 font-medium">PKCE ready</h3>
                <p className="mt-2 text-sm text-muted-foreground">
                  Generate state and optional PKCE code challenges directly in the browser.
                </p>
              </div>
              <div className="rounded-lg border p-4">
                <CheckCircle2 className="size-5 text-primary" />
                <h3 className="mt-3 font-medium">Callback hand-off</h3>
                <p className="mt-2 text-sm text-muted-foreground">
                  Route provider callbacks through Agw's backend endpoint before returning to the
                  UI.
                </p>
              </div>
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  );
}
