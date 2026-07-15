"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Cable, Puzzle, RefreshCw } from "lucide-react";
import { toast } from "sonner";

import { apiDelete, apiGet, apiPost, apiPut } from "@/api/client";
import { getApiErrorMessage } from "@/api/utils";
import { Button } from "@/components/ui/button";
import { Empty, EmptyDescription, EmptyHeader, EmptyTitle } from "@/components/ui/empty";

import { buildOAuthServerCallbackUrl } from "./callback-url";
import { ConnectionCard } from "./components/connection-card";
import { ConnectionDialog, type ConnectionEditorState } from "./components/connection-dialog";
import { PluginCard } from "./components/plugin-card";
import { PluginInstallationDialog } from "./components/plugin-installation-dialog";
import { createDefaultConnectionAlias } from "./connection-alias";
import { buildFieldPayload, createSchemaFormState, type SchemaFormState } from "./form-state";
import {
  findIntegrationSelection,
  integrationQueryKeys,
  type Connection,
  type IntegrationSelection,
  type PluginDefinition,
} from "./types";

const emptySchemaForm = (): SchemaFormState => ({ configuration: {}, secrets: {} });
const emptyConnectionEditor = (): ConnectionEditorState => ({
  alias: "",
  displayName: "",
  enabled: true,
  fields: emptySchemaForm(),
});

function redirectToAuthorization(authorizationUrl: string) {
  window.location.assign(authorizationUrl);
}

export default function IntegrationsPage() {
  const queryClient = useQueryClient();
  const [installationSelection, setInstallationSelection] =
    React.useState<IntegrationSelection | null>(null);
  const [installationEnabled, setInstallationEnabled] = React.useState(true);
  const [installationForm, setInstallationForm] = React.useState<SchemaFormState>(emptySchemaForm);
  const [connectionSelection, setConnectionSelection] = React.useState<IntegrationSelection | null>(
    null,
  );
  const [editingConnection, setEditingConnection] = React.useState<Connection | null>(null);
  const [connectionEditor, setConnectionEditor] =
    React.useState<ConnectionEditorState>(emptyConnectionEditor);

  const pluginsQuery = useQuery({
    queryKey: integrationQueryKeys.plugins,
    queryFn: async () =>
      (await apiGet("/api/integrations/plugins")) as unknown as PluginDefinition[],
  });
  const connectionsQuery = useQuery({
    queryKey: integrationQueryKeys.connections,
    queryFn: async () => (await apiGet("/api/integrations/connections")) as unknown as Connection[],
  });

  const refreshIntegrations = React.useCallback(async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: integrationQueryKeys.plugins }),
      queryClient.invalidateQueries({ queryKey: integrationQueryKeys.connections }),
    ]);
  }, [queryClient]);

  const installationMutation = useMutation({
    mutationFn: async () => {
      if (!installationSelection) throw new Error("No plugin installation selected.");
      const fields = buildFieldPayload(
        installationSelection.authScheme.installationFields,
        installationForm,
      );
      return await apiPut("/api/integrations/plugin-installations", {
        body: {
          pluginId: installationSelection.plugin.id,
          connectorId: installationSelection.connector.id,
          authSchemeId: installationSelection.authScheme.id,
          enabled: installationEnabled,
          ...fields,
        },
      });
    },
    onSuccess: async () => {
      toast.success("Plugin installation saved");
      setInstallationSelection(null);
      await refreshIntegrations();
    },
    onError: (error) => toast.error(`Save failed: ${getApiErrorMessage(error)}`),
  });

  const authorizeMutation = useMutation({
    mutationFn: async (connection: Connection) =>
      (await apiPost("/api/integrations/oauth/authorize-start", {
        body: { connectionId: connection.id, returnPath: "/integrations" },
      })) as unknown as { authorizationUrl: string },
    onSuccess: ({ authorizationUrl }) => redirectToAuthorization(authorizationUrl),
    onError: (error) => toast.error(`Authorization failed: ${getApiErrorMessage(error)}`),
  });

  const saveConnectionMutation = useMutation({
    mutationFn: async () => {
      if (!connectionSelection) throw new Error("No connection definition selected.");
      const fields = buildFieldPayload(
        connectionSelection.authScheme.connectionFields,
        connectionEditor.fields,
      );
      const common = {
        pluginId: connectionSelection.plugin.id,
        connectorId: connectionSelection.connector.id,
        authSchemeId: connectionSelection.authScheme.id,
        displayName: connectionEditor.displayName.trim(),
        alias: connectionEditor.alias.trim(),
        enabled: connectionEditor.enabled,
        ...fields,
      };
      const connection = editingConnection
        ? ((await apiPut("/api/integrations/connections", {
            body: { id: editingConnection.id, ...common },
          })) as unknown as Connection)
        : ((await apiPost("/api/integrations/connections", {
            body: common,
          })) as unknown as Connection);
      return {
        connection,
        shouldAuthorize: !editingConnection && connectionSelection.authScheme.type === "OAuth2",
      };
    },
    onSuccess: async ({ connection, shouldAuthorize }) => {
      toast.success(editingConnection ? "Connection updated" : "Connection created");
      setConnectionSelection(null);
      setEditingConnection(null);
      await refreshIntegrations();
      if (shouldAuthorize) authorizeMutation.mutate(connection);
    },
    onError: (error) => toast.error(`Save failed: ${getApiErrorMessage(error)}`),
  });

  const validateMutation = useMutation({
    mutationFn: async (connection: Connection) => {
      await apiPost("/api/integrations/connections/validate", {
        body: { id: connection.id },
      });
      return connection;
    },
    onSuccess: async (connection) => {
      toast.success(`${connection.displayName} validated`);
      await refreshIntegrations();
    },
    onError: (error) => toast.error(`Validation failed: ${getApiErrorMessage(error)}`),
  });

  const deleteMutation = useMutation({
    mutationFn: async (connection: Connection) => {
      await apiDelete("/api/integrations/connections", {
        params: { query: { id: connection.id } },
      });
      return connection;
    },
    onSuccess: async (connection) => {
      toast.success(`${connection.displayName} deleted`);
      await refreshIntegrations();
    },
    onError: (error) => toast.error(`Delete failed: ${getApiErrorMessage(error)}`),
  });

  const plugins = pluginsQuery.data ?? [];
  const connections = connectionsQuery.data ?? [];
  const callbackUrl = React.useMemo(
    () =>
      buildOAuthServerCallbackUrl({
        apiBaseUrl: process.env.NEXT_PUBLIC_API_BASE_URL,
        currentOrigin: typeof window === "undefined" ? undefined : window.location.origin,
      }),
    [],
  );

  const openInstallation = (selection: IntegrationSelection) => {
    const installation = selection.authScheme.installation;
    setInstallationSelection(selection);
    setInstallationEnabled(installation?.enabled ?? true);
    setInstallationForm(
      createSchemaFormState(selection.authScheme.installationFields, installation ?? undefined),
    );
  };
  const openCreateConnection = (selection: IntegrationSelection) => {
    setEditingConnection(null);
    setConnectionSelection(selection);
    setConnectionEditor({
      ...emptyConnectionEditor(),
      displayName: `${selection.plugin.displayName} connection`,
      alias: createDefaultConnectionAlias(selection.plugin.id),
      fields: createSchemaFormState(selection.authScheme.connectionFields),
    });
  };
  const openEditConnection = (connection: Connection) => {
    const selection = findIntegrationSelection(plugins, connection);
    if (!selection) {
      toast.error("The plugin definition for this connection is unavailable.");
      return;
    }
    setEditingConnection(connection);
    setConnectionSelection(selection);
    setConnectionEditor({
      alias: connection.alias,
      displayName: connection.displayName,
      enabled: connection.enabled,
      fields: createSchemaFormState(selection.authScheme.connectionFields, connection),
    });
  };

  return (
    <div className="w-full max-w-7xl space-y-8 py-4">
      <header className="flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between">
        <div className="space-y-2">
          <h1 className="text-3xl font-semibold tracking-tight">Integrations</h1>
          <p className="max-w-3xl text-sm leading-6 text-muted-foreground">
            Configure a plugin once, then create named connections for the exact accounts and
            endpoints Agents can use.
          </p>
        </div>
        <Button
          variant="outline"
          onClick={() => void refreshIntegrations()}
          disabled={pluginsQuery.isFetching || connectionsQuery.isFetching}
        >
          <RefreshCw
            className={
              pluginsQuery.isFetching || connectionsQuery.isFetching
                ? "size-4 animate-spin"
                : "size-4"
            }
          />
          Refresh
        </Button>
      </header>

      <section className="space-y-4">
        <div className="flex items-end justify-between gap-4">
          <div>
            <h2 className="text-xl font-semibold">Connections</h2>
            <p className="text-sm text-muted-foreground">
              Agent-selectable accounts and service endpoints, each with an immutable alias.
            </p>
          </div>
          <div className="flex items-center gap-2 text-sm text-muted-foreground">
            <Cable className="size-4" /> {connections.length}
          </div>
        </div>
        {connectionsQuery.isError ? (
          <p className="rounded-lg border border-destructive/40 p-4 text-sm text-destructive">
            Failed to load connections: {getApiErrorMessage(connectionsQuery.error)}
          </p>
        ) : connections.length > 0 ? (
          <div className="grid gap-4 lg:grid-cols-2">
            {connections.map((connection) => (
              <ConnectionCard
                key={connection.id}
                connection={connection}
                selection={findIntegrationSelection(plugins, connection)}
                isBusy={
                  (validateMutation.isPending &&
                    validateMutation.variables?.id === connection.id) ||
                  (deleteMutation.isPending && deleteMutation.variables?.id === connection.id) ||
                  (authorizeMutation.isPending && authorizeMutation.variables?.id === connection.id)
                }
                onAuthorize={(value) => authorizeMutation.mutate(value)}
                onDelete={(value) => {
                  if (
                    window.confirm(
                      `Delete ${value.displayName}? This also removes its credentials and bindings.`,
                    )
                  )
                    deleteMutation.mutate(value);
                }}
                onEdit={openEditConnection}
                onValidate={(value) => validateMutation.mutate(value)}
              />
            ))}
          </div>
        ) : (
          <Empty>
            <EmptyHeader>
              <EmptyTitle>No connections yet</EmptyTitle>
              <EmptyDescription>
                Configure a plugin below, then create the first named account or endpoint.
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        )}
      </section>

      <section className="space-y-4">
        <div className="flex items-end justify-between gap-4">
          <div>
            <h2 className="text-xl font-semibold">Plugin installations</h2>
            <p className="text-sm text-muted-foreground">
              Platform-level definitions and shared OAuth client or protocol configuration.
            </p>
          </div>
          <div className="flex items-center gap-2 text-sm text-muted-foreground">
            <Puzzle className="size-4" /> {plugins.length}
          </div>
        </div>
        {pluginsQuery.isError ? (
          <p className="rounded-lg border border-destructive/40 p-4 text-sm text-destructive">
            Failed to load plugins: {getApiErrorMessage(pluginsQuery.error)}
          </p>
        ) : (
          <div className="grid grid-cols-1 md:grid-cols-2 md:grid-cols-3 gap-4">
            {plugins.map((plugin) => (
              <PluginCard
                key={plugin.id}
                plugin={plugin}
                onConfigure={openInstallation}
                onCreateConnection={openCreateConnection}
              />
            ))}
          </div>
        )}
      </section>

      <PluginInstallationDialog
        callbackUrl={callbackUrl}
        enabled={installationEnabled}
        form={installationForm}
        isSubmitting={installationMutation.isPending}
        onEnabledChange={setInstallationEnabled}
        onFormChange={setInstallationForm}
        onOpenChange={(open) => !open && setInstallationSelection(null)}
        onSubmit={() => installationMutation.mutate()}
        open={Boolean(installationSelection)}
        selection={installationSelection}
      />
      <ConnectionDialog
        connection={editingConnection}
        editor={connectionEditor}
        isSubmitting={saveConnectionMutation.isPending}
        onEditorChange={setConnectionEditor}
        onOpenChange={(open) => {
          if (!open) {
            setConnectionSelection(null);
            setEditingConnection(null);
          }
        }}
        onSubmit={() => saveConnectionMutation.mutate()}
        open={Boolean(connectionSelection)}
        selection={connectionSelection}
      />
    </div>
  );
}
