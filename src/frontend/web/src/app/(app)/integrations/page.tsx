"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { LayoutGrid, PlugZap, RefreshCw } from "lucide-react";
import { toast } from "sonner";

import { apiDelete, apiGet, apiPost } from "@/api/client";
import { getApiErrorMessage } from "@/api/utils";
import { Button } from "@/components/ui/button";
import { Empty, EmptyDescription, EmptyHeader, EmptyTitle } from "@/components/ui/empty";

import { AppDefinitionCard } from "./components/app-definition-card";
import { AppInstanceCard } from "./components/app-instance-card";
import { buildOAuthServerCallbackUrl } from "./callback-url";
import { CreateConnectionDialog } from "./components/create-connection-dialog";
import {
  createConnectionFormState,
  getPendingOAuthSessionStorageKey,
  integrationQueryKeys,
  type AppDefinitionItem,
  type AppInstanceCreateRequest,
  type AppInstanceItem,
  type AuthorizeStartResponse,
  type CreateConnectionFormState,
  type PendingOAuthSessionState,
} from "./types";

function rememberPendingOAuthSession(
  appInstanceId: string,
  integrationId: string,
  authorizeUrl: string,
) {
  if (typeof window === "undefined") {
    return;
  }

  const state = new URL(authorizeUrl, window.location.origin).searchParams.get("state");
  if (!state) {
    throw new Error("Authorize URL did not contain a state parameter.");
  }

  const sessionState: PendingOAuthSessionState = {
    appInstanceId,
    createdAt: new Date().toISOString(),
    integrationId,
    state,
  };

  sessionStorage.setItem(
    getPendingOAuthSessionStorageKey(appInstanceId),
    JSON.stringify(sessionState),
  );
}

function redirectToAuthorizeUrl(authorizeUrl: string) {
  if (typeof window === "undefined") {
    return;
  }

  window.location.assign(authorizeUrl);
}

export default function IntegrationsPage() {
  const queryClient = useQueryClient();
  const [createOpen, setCreateOpen] = React.useState(false);
  const [selectedDefinition, setSelectedDefinition] = React.useState<AppDefinitionItem | null>(
    null,
  );
  const [createForm, setCreateForm] = React.useState<CreateConnectionFormState>(() =>
    createConnectionFormState(),
  );

  const appDefinitionsQuery = useQuery({
    queryKey: integrationQueryKeys.appDefinitions,
    queryFn: async () => {
      return (await apiGet("/api/integrations/app-definitions")) as unknown as AppDefinitionItem[];
    },
  });

  const appInstancesQuery = useQuery({
    queryKey: integrationQueryKeys.appInstances,
    queryFn: async () => {
      return (await apiGet("/api/integrations/app-instances")) as unknown as AppInstanceItem[];
    },
  });

  const createAndAuthorizeMutation = useMutation({
    mutationFn: async ({
      definition,
      form,
    }: {
      definition: AppDefinitionItem;
      form: CreateConnectionFormState;
    }) => {
      const requestBody: AppInstanceCreateRequest = {
        appName: definition.name,
        clientId: form.clientId.trim(),
        clientSecret: form.clientSecret.trim(),
        usePkce: form.usePkce,
      };

      const created = (await apiPost("/api/integrations/app-instances", {
        body: requestBody,
      })) as unknown as AppInstanceItem;

      const authorizeStart = (await apiPost(
        "/api/integrations/app-instances/{id}/authorize-start",
        {
          params: { path: { id: created.id } },
        },
      )) as unknown as AuthorizeStartResponse;

      if (!authorizeStart.authorizeUrl) {
        throw new Error("Provider authorize URL was empty.");
      }

      rememberPendingOAuthSession(created.id, definition.displayName, authorizeStart.authorizeUrl);

      return { authorizeStart, created };
    },
    onSuccess: ({ authorizeStart }) => {
      toast.success("Redirecting to provider consent");
      setCreateOpen(false);
      redirectToAuthorizeUrl(authorizeStart.authorizeUrl);
    },
    onError: (error) => {
      toast.error(`Connect failed: ${getApiErrorMessage(error)}`);
    },
    onSettled: async () => {
      await queryClient.invalidateQueries({ queryKey: integrationQueryKeys.appInstances });
    },
  });

  const reconnectMutation = useMutation({
    mutationFn: async (instance: AppInstanceItem) => {
      const payload = (await apiPost("/api/integrations/app-instances/{id}/authorize-start", {
        params: { path: { id: instance.id } },
      })) as unknown as AuthorizeStartResponse;

      if (!payload.authorizeUrl) {
        throw new Error("Provider authorize URL was empty.");
      }

      rememberPendingOAuthSession(instance.id, instance.displayName, payload.authorizeUrl);
      return { authorizeUrl: payload.authorizeUrl };
    },
    onSuccess: ({ authorizeUrl }) => {
      toast.success("Redirecting to provider consent");
      redirectToAuthorizeUrl(authorizeUrl);
    },
    onError: (error) => {
      toast.error(`Reconnect failed: ${getApiErrorMessage(error)}`);
    },
  });

  const deleteMutation = useMutation({
    mutationFn: async (instance: AppInstanceItem) => {
      await apiDelete("/api/integrations/app-instances/{id}", {
        params: { path: { id: instance.id } },
      });
      return instance;
    },
    onSuccess: async (instance) => {
      toast.success(`${instance.displayName} deleted`);
      await queryClient.invalidateQueries({ queryKey: integrationQueryKeys.appInstances });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${getApiErrorMessage(error)}`);
    },
  });

  const appDefinitions = appDefinitionsQuery.data ?? [];
  const appInstances = appInstancesQuery.data ?? [];
  const isRefreshing = appDefinitionsQuery.isFetching || appInstancesQuery.isFetching;
  const callbackUrl = React.useMemo(
    () =>
      buildOAuthServerCallbackUrl({
        apiBaseUrl: process.env.NEXT_PUBLIC_API_BASE_URL,
        currentOrigin: typeof window === "undefined" ? undefined : window.location.origin,
      }),
    [],
  );

  const openCreateDialog = (definition: AppDefinitionItem) => {
    setSelectedDefinition(definition);
    setCreateForm(createConnectionFormState(definition));
    setCreateOpen(true);
  };

  const handleDialogOpenChange = (open: boolean) => {
    setCreateOpen(open);
    if (!open) {
      setSelectedDefinition(null);
      setCreateForm(createConnectionFormState());
    }
  };

  const handleCreateAndAuthorize = () => {
    if (!selectedDefinition) {
      return;
    }

    if (!createForm.clientId.trim() || !createForm.clientSecret.trim()) {
      toast.error("Client ID and client secret are required.");
      return;
    }

    createAndAuthorizeMutation.mutate({
      definition: selectedDefinition,
      form: createForm,
    });
  };

  const handleReconnect = (instance: AppInstanceItem) => {
    reconnectMutation.mutate(instance);
  };

  const handleDelete = (instance: AppInstanceItem) => {
    const confirmed = window.confirm(
      `Delete ${instance.displayName}?\n\nThis permanently removes the app instance and stored OAuth token.`,
    );

    if (!confirmed) {
      return;
    }

    deleteMutation.mutate(instance);
  };

  const handleRefresh = async () => {
    await Promise.all([appDefinitionsQuery.refetch(), appInstancesQuery.refetch()]);
  };

  return (
    <div className="w-full max-w-7xl space-y-8 py-4">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between">
        <div className="space-y-2">
          <h1 className="text-3xl font-semibold tracking-tight">Integrations</h1>
          <p className="max-w-3xl text-sm leading-6 text-muted-foreground">
            Persist OAuth client credentials per app instance, reconnect them through a
            backend-owned authorization start endpoint, and let Agw handle token exchange on
            callback.
          </p>
        </div>

        <Button variant="outline" onClick={() => void handleRefresh()} disabled={isRefreshing}>
          <RefreshCw className={isRefreshing ? "mr-2 size-4 animate-spin" : "mr-2 size-4"} />
          Refresh
        </Button>
      </div>

      <section className="space-y-4">
        <header className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
          <div>
            <h2 className="text-xl font-semibold">Connected apps</h2>
            <p className="text-sm text-muted-foreground">
              Reconnect existing app instances or remove credentials and stored OAuth tokens.
            </p>
          </div>
          <div className="flex items-center gap-2 text-sm text-muted-foreground">
            <PlugZap className="size-4" />
            {appInstances.length} persisted instance{appInstances.length === 1 ? "" : "s"}
          </div>
        </header>

        {appInstancesQuery.isLoading ? (
          <div className="rounded-xl border border-dashed p-6 text-sm text-muted-foreground">
            Loading connected app instances...
          </div>
        ) : appInstancesQuery.isError ? (
          <div className="rounded-xl border border-dashed border-destructive/40 p-6 text-sm text-destructive">
            Failed to load app instances: {getApiErrorMessage(appInstancesQuery.error)}
          </div>
        ) : appInstances.length > 0 ? (
          <div className="grid gap-4 justify-start grid-cols-[repeat(auto-fit,minmax(280px,400px))]">
            {appInstances.map((instance) => (
              <AppInstanceCard
                key={instance.id}
                instance={instance}
                onReconnect={handleReconnect}
                onDelete={handleDelete}
                isReconnectPending={
                  reconnectMutation.isPending && reconnectMutation.variables?.id === instance.id
                }
                isDeletePending={
                  deleteMutation.isPending && deleteMutation.variables?.id === instance.id
                }
              />
            ))}
          </div>
        ) : (
          <Empty>
            <EmptyHeader>
              <EmptyTitle>No app instances yet</EmptyTitle>
              <EmptyDescription>
                Choose an app definition below to store its client credentials and launch the first
                OAuth consent flow.
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        )}
      </section>

      <section className="space-y-4">
        <header className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
          <div>
            <h2 className="text-xl font-semibold">App catalog</h2>
            <p className="text-sm text-muted-foreground">
              Every definition opens a modal with readonly provider metadata and editable client
              credentials.
            </p>
          </div>
          <div className="flex items-center gap-2 text-sm text-muted-foreground">
            <LayoutGrid className="size-4" />
            {appDefinitions.length} available definition{appDefinitions.length === 1 ? "" : "s"}
          </div>
        </header>

        {appDefinitionsQuery.isLoading ? (
          <div className="rounded-xl border border-dashed p-6 text-sm text-muted-foreground">
            Loading app definitions...
          </div>
        ) : appDefinitionsQuery.isError ? (
          <div className="rounded-xl border border-dashed border-destructive/40 p-6 text-sm text-destructive">
            Failed to load app definitions: {getApiErrorMessage(appDefinitionsQuery.error)}
          </div>
        ) : appDefinitions.length > 0 ? (
          <div className="grid gap-4 justify-start grid-cols-[repeat(auto-fit,minmax(280px,400px))]">
            {appDefinitions.map((definition) => (
              <AppDefinitionCard
                key={definition.name}
                definition={definition}
                onSelect={openCreateDialog}
              />
            ))}
          </div>
        ) : (
          <Empty>
            <EmptyHeader>
              <EmptyTitle>No app definitions found</EmptyTitle>
              <EmptyDescription>
                The backend did not return any available integrations for this environment.
              </EmptyDescription>
            </EmptyHeader>
          </Empty>
        )}
      </section>

      <CreateConnectionDialog
        callbackUrl={callbackUrl}
        open={createOpen}
        onOpenChange={handleDialogOpenChange}
        definition={selectedDefinition}
        form={createForm}
        onFormChange={setCreateForm}
        onSubmit={handleCreateAndAuthorize}
        isSubmitting={createAndAuthorizeMutation.isPending}
      />
    </div>
  );
}
