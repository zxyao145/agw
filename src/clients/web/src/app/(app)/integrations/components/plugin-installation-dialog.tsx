"use client";

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
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";

import type { SchemaFormState } from "../form-state";
import type { IntegrationSelection } from "../types";
import { SchemaFields } from "./schema-fields";

type PluginInstallationDialogProps = {
  callbackUrl: string;
  enabled: boolean;
  form: SchemaFormState;
  isSubmitting: boolean;
  onEnabledChange: (enabled: boolean) => void;
  onFormChange: (form: SchemaFormState) => void;
  onOpenChange: (open: boolean) => void;
  onSubmit: () => void;
  open: boolean;
  selection: IntegrationSelection | null;
};

export function PluginInstallationDialog({
  callbackUrl,
  enabled,
  form,
  isSubmitting,
  onEnabledChange,
  onFormChange,
  onOpenChange,
  onSubmit,
  open,
  selection,
}: PluginInstallationDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="lg" className="max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>Plugin installation</DialogTitle>
          <DialogDescription>
            Configure platform-level credentials shared by connections using this authentication
            scheme.
          </DialogDescription>
        </DialogHeader>

        {selection ? (
          <div className="grid gap-5">
            <div className="flex flex-wrap items-center gap-2 rounded-lg border bg-muted/20 p-4">
              <span className="font-medium">{selection.plugin.displayName}</span>
              <Badge variant="outline">{selection.connector.displayName}</Badge>
              <Badge variant="outline">{selection.authScheme.displayName}</Badge>
            </div>
            <div className="flex items-center justify-between rounded-lg border px-4 py-3">
              <div>
                <Label htmlFor="installation-enabled">Installation enabled</Label>
                <p className="mt-1 text-xs text-muted-foreground">
                  Disabling it makes all connections from this plugin unavailable.
                </p>
              </div>
              <Switch
                id="installation-enabled"
                checked={enabled}
                onCheckedChange={onEnabledChange}
              />
            </div>
            <SchemaFields
              fields={selection.authScheme.installationFields}
              form={form}
              idPrefix="installation"
              onChange={onFormChange}
            />
            {selection.authScheme.type === "OAuth2" ? (
              <div className="rounded-lg border border-dashed bg-muted/20 p-4 text-sm">
                <p className="font-medium">OAuth callback URL</p>
                <p className="mt-1 break-all font-mono text-xs text-muted-foreground">
                  {callbackUrl}
                </p>
              </div>
            ) : null}
          </div>
        ) : null}

        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button type="button" onClick={onSubmit} disabled={!selection || isSubmitting}>
            {isSubmitting ? "Saving..." : "Save installation"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
