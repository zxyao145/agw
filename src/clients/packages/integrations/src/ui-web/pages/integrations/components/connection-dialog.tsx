"use client";

import { Badge } from "@agw/components";
import { Button } from "@agw/components";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@agw/components";
import { Input } from "@agw/components";
import { Label } from "@agw/components";
import { Switch } from "@agw/components";

import { isConnectionAliasValid } from "../connection-alias";
import type { SchemaFormState } from "../form-state";
import type { Connection, IntegrationSelection } from "../types";
import { SchemaFields } from "./schema-fields";

export type ConnectionEditorState = {
  alias: string;
  displayName: string;
  enabled: boolean;
  fields: SchemaFormState;
};

type ConnectionDialogProps = {
  connection: Connection | null;
  editor: ConnectionEditorState;
  isSubmitting: boolean;
  onEditorChange: (editor: ConnectionEditorState) => void;
  onOpenChange: (open: boolean) => void;
  onSubmit: () => void;
  open: boolean;
  selection: IntegrationSelection | null;
};

export function ConnectionDialog({
  connection,
  editor,
  isSubmitting,
  onEditorChange,
  onOpenChange,
  onSubmit,
  open,
  selection,
}: ConnectionDialogProps) {
  const aliasValid = isConnectionAliasValid(editor.alias);
  const aliasInvalid = editor.alias.length > 0 && !aliasValid;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="lg" className="max-h-[90vh] overflow-y-auto agw-scrollbar">
        <DialogHeader>
          <DialogTitle>{connection ? "Edit integration" : "Create integration"}</DialogTitle>
          <DialogDescription>
            A configured integration identifies one exact account or endpoint that an Agent can
            select by alias.
          </DialogDescription>
        </DialogHeader>

        {selection ? (
          <div className="grid gap-5">
            <div className="flex flex-wrap items-center gap-2 rounded-lg border bg-muted/20 p-4">
              <span className="font-medium">{selection.plugin.displayName}</span>
              <Badge variant="outline">{selection.connector.displayName}</Badge>
              <Badge variant="outline">{selection.authScheme.displayName}</Badge>
            </div>
            <div className="grid items-start gap-4 sm:grid-cols-2">
              <div className="grid gap-2">
                <Label htmlFor="connection-display-name">Display name *</Label>
                <Input
                  id="connection-display-name"
                  value={editor.displayName}
                  onChange={(event) =>
                    onEditorChange({ ...editor, displayName: event.target.value })
                  }
                  placeholder="GitHub work"
                />
              </div>
              <div className="grid gap-2">
                <Label htmlFor="connection-alias">Alias *</Label>
                <Input
                  id="connection-alias"
                  value={editor.alias}
                  onChange={(event) => onEditorChange({ ...editor, alias: event.target.value })}
                  placeholder="github-work"
                  readOnly={Boolean(connection)}
                  aria-invalid={aliasInvalid}
                  aria-describedby="connection-alias-help"
                  className={connection ? "bg-muted/50 font-mono" : "font-mono"}
                />
                <div id="connection-alias-help" className="space-y-1 text-xs">
                  {aliasInvalid ? (
                    <p className="text-destructive">Use lowercase letters, numbers, and hyphens.</p>
                  ) : null}
                  <p className="text-muted-foreground">
                    Tool namespace: {editor.alias || "alias"}__operation. Alias cannot be changed.
                  </p>
                </div>
              </div>
            </div>
            <div className="flex items-center justify-between rounded-lg border px-4 py-3">
              <Label htmlFor="connection-enabled">Integration enabled</Label>
              <Switch
                id="connection-enabled"
                checked={editor.enabled}
                onCheckedChange={(enabled) => onEditorChange({ ...editor, enabled })}
              />
            </div>
            <SchemaFields
              fields={selection.authScheme.connectionFields}
              form={editor.fields}
              idPrefix="connection"
              onChange={(fields) => onEditorChange({ ...editor, fields })}
            />
            {selection.authScheme.type === "OAuth2" ? (
              <p className="rounded-lg border border-dashed bg-muted/20 p-4 text-sm text-muted-foreground">
                Save first, then Agw will open the provider consent screen. Reauthorization is also
                available from the integration card.
              </p>
            ) : null}
          </div>
        ) : null}

        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            type="button"
            onClick={onSubmit}
            disabled={!selection || !editor.displayName.trim() || !aliasValid || isSubmitting}
          >
            {isSubmitting ? "Saving..." : connection ? "Save changes" : "Create integration"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
