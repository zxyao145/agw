"use client";

import * as React from "react";
import { Copy, LockKeyhole, ShieldCheck } from "lucide-react";
import { toast } from "sonner";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";

import {
  formatIntegrationCategory,
  type AppDefinitionItem,
  type CreateConnectionFormState,
} from "../types";

type CreateConnectionDialogProps = {
  callbackUrl: string;
  definition: AppDefinitionItem | null;
  form: CreateConnectionFormState;
  isSubmitting?: boolean;
  onFormChange: React.Dispatch<React.SetStateAction<CreateConnectionFormState>>;
  onOpenChange: (open: boolean) => void;
  onSubmit: () => void;
  open: boolean;
};

export function CreateConnectionDialog({
  callbackUrl,
  definition,
  form,
  isSubmitting = false,
  onFormChange,
  onOpenChange,
  onSubmit,
  open,
}: CreateConnectionDialogProps) {
  const [_, setIsCopyingCallbackUrl] = React.useState(false);

  const handleSubmit = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    onSubmit();
  };

  const handleCopyCallbackUrl = async () => {
    try {
      setIsCopyingCallbackUrl(true);
      await navigator.clipboard.writeText(callbackUrl);
      toast.success("Callback URL copied");
    } catch {
      toast.error("Unable to copy callback URL");
    } finally {
      setIsCopyingCallbackUrl(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="xl" className="flex max-h-[90vh] flex-col overflow-hidden gap-3">
        <DialogHeader>
          <DialogTitle>OAuth connection</DialogTitle>
          <DialogDescription>
            Persist client credentials first, then let Agw launch the provider consent screen and
            handle the callback on the backend.
          </DialogDescription>
        </DialogHeader>

        {definition ? (
          <form onSubmit={handleSubmit} className="min-h-0 flex-1 overflow-y-auto pr-1">
            <div className="grid gap-3 lg:grid-cols-[0.7fr_1.3fr]">
              <section className="space-y-4 rounded-xl border border-dashed bg-muted/15 p-3">
                <div>
                  <h2 className="text-lg font-semibold">{definition.displayName}</h2>
                  <div className="flex flex-wrap items-center gap-2">
                    <Badge variant="outline">
                      {formatIntegrationCategory(definition.category)}
                    </Badge>
                    <Badge variant="outline">{definition.provider}</Badge>
                  </div>

                  <p className="mt-2 text-sm leading-6 text-muted-foreground">
                    {definition.description}
                  </p>
                </div>

                <div className="grid gap-4">
                  <div className="space-y-2">
                    <Label htmlFor="definition-auth-url">Authorization URL</Label>
                    <div
                      id="definition-auth-url"
                      className="text-sm truncate mr-2 text-muted-foreground"
                    >
                      {definition.authUrl}
                    </div>
                  </div>

                  <div className="space-y-2">
                    <Label>Scopes</Label>
                    <div className="flex flex-wrap gap-1.5 rounded-lg border border-dashed bg-background p-3">
                      {definition.scopes.map((scope) => (
                        <Badge key={scope} variant="outline">
                          {scope}
                        </Badge>
                      ))}
                    </div>
                  </div>
                  <div className="grid gap-4 sm:grid-cols-1">
                    <div className="space-y-2">
                      <Label>Tags</Label>
                      <div className="rounded-lg border border-dashed bg-background p-3 text-sm text-muted-foreground">
                        {definition.tags.length > 0 ? definition.tags.join(", ") : "No tags"}
                      </div>
                    </div>
                    <div className="space-y-2">
                      <Label>Related tools</Label>
                      <div className="rounded-lg border border-dashed bg-background p-3 text-sm text-muted-foreground">
                        {definition.toolNames.length > 0
                          ? definition.toolNames.join(", ")
                          : "No related tools"}
                      </div>
                    </div>
                  </div>
                </div>
              </section>

              <section className="space-y-4 rounded-xl border bg-background p-3">
                <div className="space-y-2">
                  <h2 className="text-lg font-semibold">Connection credentials</h2>
                  <p className="text-sm text-muted-foreground">
                    These fields are stored in the persisted app instance and used for every future
                    reconnect.
                  </p>
                </div>

                <div className="space-y-4">
                  <div className="space-y-2">
                    <Label htmlFor="client-id">Client ID</Label>
                    <Input
                      id="client-id"
                      value={form.clientId}
                      onChange={(event) =>
                        onFormChange((current) => ({
                          ...current,
                          clientId: event.target.value,
                        }))
                      }
                      placeholder="your-provider-client-id"
                      autoComplete="off"
                    />
                  </div>

                  <div className="space-y-2">
                    <Label htmlFor="client-secret">Client secret</Label>
                    <div className="relative">
                      <Input
                        id="client-secret"
                        type="password"
                        value={form.clientSecret}
                        onChange={(event) =>
                          onFormChange((current) => ({
                            ...current,
                            clientSecret: event.target.value,
                          }))
                        }
                        placeholder="Paste the provider secret"
                        autoComplete="new-password"
                        className="pr-10"
                      />
                      <LockKeyhole className="pointer-events-none absolute top-1/2 right-3 size-4 -translate-y-1/2 text-muted-foreground" />
                    </div>
                  </div>

                  <div className="flex items-center justify-between rounded-lg border border-dashed bg-muted/15 px-4 py-3">
                    <div>
                      <Label htmlFor="use-pkce" className="text-sm font-medium">
                        Use PKCE
                      </Label>
                      <p className="mt-1 text-xs text-muted-foreground">
                        Keep this enabled unless the provider configuration explicitly requires a
                        non-PKCE confidential client flow.
                      </p>
                    </div>
                    <Switch
                      id="use-pkce"
                      checked={form.usePkce}
                      onCheckedChange={(checked) =>
                        onFormChange((current) => ({
                          ...current,
                          usePkce: checked,
                        }))
                      }
                    />
                  </div>

                  <div className="rounded-lg border border-primary/15 bg-primary/5 p-4 text-sm text-muted-foreground">
                    <div className="mb-2 flex items-center gap-2 font-medium text-foreground">
                      <ShieldCheck className="size-4 text-primary" />
                      Backend-owned callback flow
                    </div>
                    Agw will redirect through <code>/api/integrations/oauth/callback</code>, store
                    the token against this app instance, and then hand the browser back to the UI.
                  </div>
                </div>
              </section>
            </div>
          </form>
        ) : null}

        <div className="space-y-1 mb-3">
          <div className="text-sm font-medium tracking-[0.05em]">OAuth callback URL:</div>
          <div className="relative flex items-center justify-between">
            <span className="text-xs truncate mr-2 text-muted-foreground">{callbackUrl}</span>
            <Copy className="size-4" onClick={() => void handleCopyCallbackUrl()} />
          </div>
        </div>

        <DialogFooter>
          <Button type="button" size="sm" variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button type="button" size="sm" onClick={onSubmit} disabled={!definition || isSubmitting}>
            {isSubmitting ? "Connecting..." : "Connect"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
