"use client";

import { RefreshCw, Trash2, UserRound } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";

import {
  formatDateTime,
  formatIntegrationCategory,
  getAuthorizationState,
  type AppInstanceItem,
} from "../types";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

type AppInstanceCardProps = {
  instance: AppInstanceItem;
  isDeletePending?: boolean;
  isReconnectPending?: boolean;
  onDelete: (instance: AppInstanceItem) => void;
  onReconnect: (instance: AppInstanceItem) => void;
};

export function AppInstanceCard({
  instance,
  isDeletePending = false,
  isReconnectPending = false,
  onDelete,
  onReconnect,
}: AppInstanceCardProps) {
  const authorizationState = getAuthorizationState(instance);

  return (
    <Card className="gap-0 border-border/70 bg-gradient-to-br from-background via-background to-muted/35 py-0 max-w-100">
      <CardHeader className="border-b border-dashed p-3 [.border-b]:pb-3">
        <div className="flex flex-col flex-wrap items-start justify-between">
          <div className="space-y-2">
            <CardTitle>
              <div className="flex gap-2 justify-start items-center">
                <span className="text-base">{instance.displayName}</span>
                <Badge
                  className="font-mono text-xs"
                  variant={authorizationState.variant}
                >
                  {authorizationState.label}
                </Badge>
              </div>
            </CardTitle>
            <div className="flex flex-wrap items-center gap-2">
              <Badge variant="secondary">
                {formatIntegrationCategory(instance.category)}
              </Badge>
              <Badge variant="outline">{instance.provider}</Badge>
            </div>
          </div>
        </div>
      </CardHeader>

      <CardContent className="grid gap-2 p-3 md:grid-cols-1">
        <div className="rounded-lg border border-dashed bg-muted/20 p-4">
          <div className="mb-2 text-xs font-medium uppercase tracking-[0.16em] text-muted-foreground">
            Credentials
          </div>
          <dl className="space-y-2 text-sm">
            <div className="flex items-center justify-between gap-3">
              <dt className="text-muted-foreground">Client ID</dt>
              <dd className="truncate font-mono text-xs">
                {instance.clientId}
              </dd>
            </div>
            <div className="flex items-center justify-between gap-3">
              <dt className="text-muted-foreground">Client secret</dt>
              <dd>{instance.hasClientSecret ? "Stored" : "Missing"}</dd>
            </div>
          </dl>
        </div>

        <div className="rounded-lg border border-dashed bg-muted/20 p-4">
          <div className="mb-2 text-xs font-medium uppercase tracking-[0.16em] text-muted-foreground">
            Authorization
          </div>
          <dl className="space-y-2 text-sm">
            <div className="flex items-center justify-between gap-3">
              <dt className="flex items-center gap-1 text-muted-foreground">
                <UserRound className="size-3.5" />
                Subject
              </dt>
              <Tooltip>
                <TooltipTrigger asChild>
                  <dd className="truncate">
                    {instance.authorizationSubject ?? "Not granted yet"}
                  </dd>
                </TooltipTrigger>
                <TooltipContent>
                  {instance.authorizationSubject ?? "Not granted yet"}
                </TooltipContent>
              </Tooltip>
            </div>
            <div className="flex items-center justify-between gap-3">
              <dt className="text-muted-foreground">Expires</dt>
              <dd>{formatDateTime(instance.authorizationExpiresAtUtc)}</dd>
            </div>
            <div className="flex items-center justify-between gap-3">
              <dt className="text-muted-foreground">Reconnect</dt>
              <dd>{instance.isAuthorized ? "Optional" : "Recommended"}</dd>
            </div>
          </dl>
        </div>
      </CardContent>

      <CardFooter className="justify-between gap-3 border-t border-dashed p-3 [.border-t]:pt-3">
        <div className="flex items-center gap-2 text-sm text-muted-foreground"></div>
        <div className="flex flex-wrap justify-end gap-2">
          <Button
            type="button"
            className="text-sm"
            size="sm"
            variant="outline"
            onClick={() => onReconnect(instance)}
            disabled={isReconnectPending || isDeletePending}
          >
            <RefreshCw
              className={isReconnectPending ? "size-4 animate-spin" : "size-4"}
            />
            Reconnect
          </Button>
          <Button
            type="button"
            className="text-sm"
            size="sm"
            variant="destructive"
            onClick={() => onDelete(instance)}
            disabled={isDeletePending || isReconnectPending}
          >
            <Trash2 className="size-4" />
            Delete
          </Button>
        </div>
      </CardFooter>
    </Card>
  );
}
