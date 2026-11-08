"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@agw/components/query";
import { RefreshCw } from "lucide-react";

import { getAuthSession } from "@agw/auth";
import { getApiErrorMessage } from "@agw/api";
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@agw/components";

import {
  listServerExtensions,
  setServerExtensionEnabled,
  type ServerExtensionStatus,
} from "../services/server-extensions";

const EXTENSIONS_QUERY_KEY = ["server-extensions"] as const;
const EXTENSIONS_REFRESH_INTERVAL_MS = 5000;

export function ServerExtensionsPage() {
  const sessionQuery = useQuery({
    queryKey: ["server-extension-admin-session"],
    queryFn: getAuthSession,
    staleTime: 0,
  });

  if (sessionQuery.isPending || sessionQuery.isFetching) {
    return <p className="text-sm text-muted-foreground">Checking administrator access…</p>;
  }

  if (sessionQuery.isError) {
    return (
      <section className="space-y-2" role="alert">
        <h1 className="text-xl font-semibold">Server extensions</h1>
        <p className="text-sm text-destructive">
          Unable to verify administrator access: {getApiErrorMessage(sessionQuery.error)}
        </p>
        <Button variant="outline" onClick={() => void sessionQuery.refetch()}>
          Try again
        </Button>
      </section>
    );
  }

  if (!sessionQuery.data.isAdmin) {
    return (
      <section className="space-y-2" role="alert">
        <h1 className="text-xl font-semibold">Server extensions</h1>
        <p className="text-sm text-muted-foreground">
          Administrator access is required to manage server extensions.
        </p>
      </section>
    );
  }

  return <ServerExtensionsAdmin />;
}

function ServerExtensionsAdmin() {
  const queryClient = useQueryClient();
  const [enablementError, setEnablementError] = React.useState<string | null>(null);
  const extensionsQuery = useQuery({
    queryKey: EXTENSIONS_QUERY_KEY,
    queryFn: listServerExtensions,
    refetchInterval: EXTENSIONS_REFRESH_INTERVAL_MS,
  });
  const enablementMutation = useMutation({
    mutationFn: ({ extensionId, enabled }: { extensionId: string; enabled: boolean }) =>
      setServerExtensionEnabled(extensionId, enabled),
    retry: false,
    onSuccess: (status) => {
      setEnablementError(null);
      queryClient.setQueryData<ServerExtensionStatus[]>(EXTENSIONS_QUERY_KEY, (current) =>
        current?.map((extension) => (extension.id === status.id ? status : extension)),
      );
      void queryClient.invalidateQueries({ queryKey: EXTENSIONS_QUERY_KEY });
    },
    onError: (error) => {
      setEnablementError(getApiErrorMessage(error));
      void queryClient.invalidateQueries({ queryKey: EXTENSIONS_QUERY_KEY });
    },
  });

  const extensions = extensionsQuery.data ?? [];
  const updatingExtensionId = enablementMutation.isPending
    ? enablementMutation.variables.extensionId
    : null;

  function updateEnablement(extensionId: string, enabled: boolean) {
    setEnablementError(null);
    enablementMutation.mutate({ extensionId, enabled });
  }

  return (
    <div className="w-full max-w-7xl space-y-6 py-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="text-xl font-semibold">Server extensions</h1>
          <p className="text-sm text-muted-foreground">
            Extensions discovered at Server startup. Enablement applies across Hosts.
          </p>
        </div>
        <Button
          type="button"
          variant="outline"
          onClick={() => void extensionsQuery.refetch()}
          disabled={extensionsQuery.isFetching || enablementMutation.isPending}
        >
          <RefreshCw aria-hidden="true" className="mr-2 h-4 w-4" />
          Refresh
        </Button>
      </div>

      {enablementError && (
        <p className="text-sm text-destructive" role="alert">
          Failed to update extension enablement: {enablementError}
        </p>
      )}

      <Card>
        <CardHeader>
          <CardTitle>Extension inventory</CardTitle>
          <CardDescription>
            Shared enablement and the current Host runtime status are shown for each discovered
            extension.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {extensionsQuery.isPending ? (
            <p className="text-sm text-muted-foreground">Loading server extensions…</p>
          ) : extensionsQuery.isError ? (
            <div className="space-y-2" role="alert">
              <p className="text-sm text-destructive">
                Failed to load server extensions: {getApiErrorMessage(extensionsQuery.error)}
              </p>
              <Button variant="outline" onClick={() => void extensionsQuery.refetch()}>
                Try again
              </Button>
            </div>
          ) : extensions.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              No extensions were discovered at Server startup.
            </p>
          ) : (
            <div className="overflow-x-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Extension</TableHead>
                    <TableHead>Targets</TableHead>
                    <TableHead>Version</TableHead>
                    <TableHead>Shared enablement</TableHead>
                    <TableHead>Current Host</TableHead>
                    <TableHead>Activity</TableHead>
                    <TableHead className="text-right">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {extensions.map((extension) => (
                    <ExtensionRow
                      key={extension.id}
                      extension={extension}
                      updating={updatingExtensionId === extension.id}
                      disabled={enablementMutation.isPending}
                      onEnablementChange={updateEnablement}
                    />
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function ExtensionRow({
  extension,
  updating,
  disabled,
  onEnablementChange,
}: {
  extension: ServerExtensionStatus;
  updating: boolean;
  disabled: boolean;
  onEnablementChange(extensionId: string, enabled: boolean): void;
}) {
  const stateVariant =
    extension.state === "Failed"
      ? "destructive"
      : extension.state === "Active"
        ? "default"
        : "secondary";

  return (
    <TableRow>
      <TableCell className="min-w-52">
        <div className="font-medium">{extension.displayName}</div>
        <div className="text-xs text-muted-foreground">{extension.name}</div>
      </TableCell>
      <TableCell>{formatTargets(extension.targets)}</TableCell>
      <TableCell>{extension.currentVersion || "—"}</TableCell>
      <TableCell>
        <Badge variant={extension.isEnabled ? "default" : "secondary"}>
          {extension.isEnabled ? "Enabled" : "Disabled"}
        </Badge>
      </TableCell>
      <TableCell className="min-w-48">
        <Badge variant={stateVariant}>{extension.state}</Badge>
        {(extension.errorCode != null || extension.errorMessage) && (
          <p className="mt-1 text-xs text-destructive">
            {extension.errorCode != null && `Error ${extension.errorCode}`}
            {extension.errorCode != null && extension.errorMessage ? ": " : ""}
            {extension.errorMessage}
          </p>
        )}
      </TableCell>
      <TableCell className="min-w-40 text-xs text-muted-foreground">
        <div>Active invocations: {extension.activeInvocations ?? 0}</div>
        <div>Active leases: {extension.activeLeases ?? 0}</div>
      </TableCell>
      <TableCell className="text-right">
        <Button
          type="button"
          variant="outline"
          size="sm"
          aria-label={`${extension.isEnabled ? "Disable" : "Enable"} ${extension.displayName}`}
          disabled={disabled}
          onClick={() => onEnablementChange(extension.id, !extension.isEnabled)}
        >
          {updating ? "Updating…" : extension.isEnabled ? "Disable" : "Enable"}
        </Button>
      </TableCell>
    </TableRow>
  );
}

function formatTargets(targets: ServerExtensionStatus["targets"]): string {
  const names = [
    (targets & 1) !== 0 ? "Server" : null,
    (targets & 2) !== 0 ? "Desktop" : null,
  ].filter((name): name is string => name !== null);
  return names.length > 0 ? names.join(", ") : "Unknown";
}
