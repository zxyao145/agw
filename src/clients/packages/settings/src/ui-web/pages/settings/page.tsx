"use client";

import * as React from "react";
import { Copy, KeyRound, LogOut, Plus, Trash2 } from "lucide-react";
import { toast } from "sonner";

import {
  changePassword,
  createApiToken,
  listApiTokens,
  logout,
  revokeApiToken,
  type ApiTokenSummary,
  type CreatedApiToken,
} from "@agw/auth";
import {
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Input,
  Label,
  formatLocalDateTime,
} from "@agw/components";

function encodeBase64Config(token: string): string {
  const bytes = new TextEncoder().encode(
    JSON.stringify({
      version: 2,
      serverUrl: window.location.origin,
      token,
      apiMajorVersion: 1,
    }),
  );
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/g, "");
}

export default function SettingsPage() {
  const [tokens, setTokens] = React.useState<ApiTokenSummary[]>([]);
  const [name, setName] = React.useState("");
  const [created, setCreated] = React.useState<CreatedApiToken | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [currentPassword, setCurrentPassword] = React.useState("");
  const [newPassword, setNewPassword] = React.useState("");

  const refresh = React.useCallback(async () => setTokens(await listApiTokens()), []);
  React.useEffect(() => {
    void refresh();
  }, [refresh]);

  const handleCreate = async () => {
    setBusy(true);
    try {
      const result = await createApiToken(name.trim());
      setCreated(result);
      setName("");
      await refresh();
    } catch {
      toast.error("Unable to create API token");
    } finally {
      setBusy(false);
    }
  };

  const copyBase64Config = async () => {
    if (!created) return;
    await navigator.clipboard.writeText(encodeBase64Config(created.token));
    toast.success("Base64 configuration copied");
  };

  return (
    <div className="w-full max-w-4xl space-y-6 py-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <p className="text-xs font-semibold uppercase tracking-[0.12em] text-primary">Security</p>
          <h1 className="mt-1 text-2xl font-semibold">Server access</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Issue a separate token for each Desktop, Mobile or automation client.
          </p>
        </div>
        <Button
          variant="outline"
          onClick={async () => {
            await logout();
            window.location.assign("/login/");
          }}
        >
          <LogOut className="mr-2 h-4 w-4" />
          Sign out
        </Button>
      </div>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <KeyRound className="h-5 w-5" />
            API tokens
          </CardTitle>
          <CardDescription>
            Token secrets are shown once and stored by the Server only as hashes.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-5">
          <div className="flex gap-2">
            <div className="flex-1 space-y-2">
              <Label htmlFor="token-name">Token name</Label>
              <div className="flex gap-2">
                <Input
                  id="token-name"
                  placeholder="Ben’s MacBook"
                  maxLength={64}
                  value={name}
                  onChange={(event) => setName(event.target.value)}
                />
                <Button disabled={busy || name.trim().length === 0} onClick={handleCreate}>
                  <Plus className="h-4 w-4" />
                  Create
                </Button>
              </div>
            </div>
          </div>

          {created ? (
            <div className="rounded-lg border border-amber-500/40 bg-amber-500/5 p-4">
              <p className="text-sm font-medium">
                Copy this secret now. It will not be shown again.
              </p>
              <code className="mt-2 block break-all rounded-md bg-background p-3 text-xs">
                {created.token}
              </code>
              <div className="mt-3 flex gap-2">
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => navigator.clipboard.writeText(created.token)}
                >
                  <Copy className="mr-2 h-4 w-4" />
                  Copy token
                </Button>
                <Button size="sm" onClick={copyBase64Config}>
                  <Copy className="mr-2 h-4 w-4" />
                  Copy config
                </Button>
              </div>
            </div>
          ) : null}

          <div className="divide-y rounded-lg border">
            {tokens.length === 0 ? (
              <p className="p-4 text-sm text-muted-foreground">No client tokens yet.</p>
            ) : (
              tokens.map((token) => (
                <div key={token.id} className="flex items-center justify-between gap-4 p-4">
                  <div>
                    <p className="font-medium">{token.name}</p>
                    <p className="font-mono text-xs text-muted-foreground">
                      {token.prefix}… · {formatLocalDateTime(token.createdAt)}
                    </p>
                  </div>
                  <Button
                    size="icon"
                    variant="ghost"
                    aria-label={`Revoke ${token.name}`}
                    onClick={async () => {
                      await revokeApiToken(token.id);
                      await refresh();
                    }}
                  >
                    <Trash2 className="h-4 w-4 text-destructive" />
                  </Button>
                </div>
              ))
            )}
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Administrator password</CardTitle>
          <CardDescription>
            Changing the password invalidates every existing Web session. Locally trusted access may
            leave the current password empty.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="current-password">Current password</Label>
            <Input
              id="current-password"
              type="password"
              autoComplete="current-password"
              value={currentPassword}
              onChange={(event) => setCurrentPassword(event.target.value)}
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="new-password">New password</Label>
            <Input
              id="new-password"
              type="password"
              autoComplete="new-password"
              minLength={8}
              maxLength={256}
              value={newPassword}
              onChange={(event) => setNewPassword(event.target.value)}
            />
          </div>
          <Button
            variant="outline"
            disabled={newPassword.length < 8}
            onClick={async () => {
              await changePassword(currentPassword, newPassword);
              window.location.assign("/login/");
            }}
          >
            Change password
          </Button>
        </CardContent>
      </Card>
    </div>
  );
}
