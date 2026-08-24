"use client";

import { CheckCircle2, KeyRound, Pencil, RefreshCw, RotateCcw, Trash2 } from "lucide-react";

import { Badge } from "@agw/components";
import { Button } from "@agw/components";
import { Card, CardContent, CardFooter, CardHeader, CardTitle } from "@agw/components";
import { formatLocalDateTime } from "@agw/components";

import type { Connection, IntegrationSelection } from "../types";
import { connectionStatusPresentation } from "../types";

type ConnectionCardProps = {
  connection: Connection;
  isBusy: boolean;
  onAuthorize: (connection: Connection) => void;
  onDelete: (connection: Connection) => void;
  onEdit: (connection: Connection) => void;
  onValidate: (connection: Connection) => void;
  selection: IntegrationSelection | null;
};

export function ConnectionCard({
  connection,
  isBusy,
  onAuthorize,
  onDelete,
  onEdit,
  onValidate,
  selection,
}: ConnectionCardProps) {
  const status = connectionStatusPresentation(connection.status);
  const isOAuth = selection?.authScheme.type === "OAuth2";
  const warning =
    connection.lastValidationErrorCode ??
    (connection.status === "Ready"
      ? null
      : "This integration does not currently contribute tools or skills.");

  return (
    <Card className="gap-0 overflow-hidden py-0">
      <CardHeader className="border-b border-dashed bg-muted/20 p-5 [.border-b]:pb-5">
        <CardTitle className="flex flex-wrap items-center gap-2">
          <span>{connection.displayName}</span>
          <Badge variant={status.variant}>{status.label}</Badge>
        </CardTitle>
        <div className="flex flex-wrap gap-2 text-xs text-muted-foreground">
          <span className="font-mono">{connection.alias}</span>
          <span>·</span>
          <span>{selection?.connector.displayName ?? connection.connectorId}</span>
          <span>·</span>
          <span>{selection?.authScheme.displayName ?? connection.authSchemeId}</span>
        </div>
      </CardHeader>
      <CardContent className="grid gap-4 p-5 sm:grid-cols-2">
        <dl className="grid gap-2 text-sm">
          <div className="flex justify-between gap-3">
            <dt className="text-muted-foreground">Subject</dt>
            <dd className="truncate">{connection.subject ?? "Not available"}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-muted-foreground">Expires</dt>
            <dd>{formatLocalDateTime(connection.expiresAtUtc)}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-muted-foreground">Last checked</dt>
            <dd>{formatLocalDateTime(connection.lastValidatedAtUtc)}</dd>
          </div>
        </dl>
        <div className="rounded-lg border border-dashed p-3 text-xs text-muted-foreground">
          <div className="mb-2 flex items-center gap-2 font-medium text-foreground">
            {connection.status === "Ready" ? (
              <CheckCircle2 className="size-4 text-emerald-600" />
            ) : (
              <KeyRound className="size-4" />
            )}
            Security state
          </div>
          {warning ?? "Credentials are configured. Secret values are never returned by the API."}
        </div>
      </CardContent>
      <CardFooter className="flex flex-wrap justify-end gap-2 border-t border-dashed p-4 [.border-t]:pt-4">
        <Button
          variant="outline"
          size="sm"
          onClick={() => onValidate(connection)}
          disabled={isBusy}
        >
          <RefreshCw className={isBusy ? "size-4 animate-spin" : "size-4"} />
          Validate
        </Button>
        {isOAuth ? (
          <Button
            variant="outline"
            size="sm"
            onClick={() => onAuthorize(connection)}
            disabled={isBusy}
          >
            <RotateCcw className="size-4" />
            Authorize
          </Button>
        ) : null}
        <Button variant="outline" size="sm" onClick={() => onEdit(connection)} disabled={isBusy}>
          <Pencil className="size-4" />
          Edit / rotate
        </Button>
        <Button
          variant="destructive"
          size="sm"
          onClick={() => onDelete(connection)}
          disabled={isBusy}
        >
          <Trash2 className="size-4" />
          Delete
        </Button>
      </CardFooter>
    </Card>
  );
}
