"use client";

import * as React from "react";
import { useQuery } from "@tanstack/react-query";

import { apiGet } from "@/api/client";
import { Button } from "@/components/ui/button";
import type { ModelProviderDto, ModelDto, ProviderDto } from "./components/types";
import { ModelProviderTable } from "./components/model-provider-table";
import { CreateModelProviderDialog } from "./components/create-model-provider-dialog";

export default function ModelProvidersPage() {
  const modelProvidersQuery = useQuery({
    queryKey: ["model-providers"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas for 200 bodies.
      return (await apiGet("/api/model-providers")) as unknown as ModelProviderDto[];
    },
  });

  const modelsQuery = useQuery({
    queryKey: ["models"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas for 200 bodies.
      return (await apiGet("/api/models")) as unknown as ModelDto[];
    },
  });

  const providersQuery = useQuery({
    queryKey: ["providers"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas for 200 bodies.
      return (await apiGet("/api/providers")) as unknown as ProviderDto[];
    },
  });

  const modelNameById = React.useMemo(() => {
    return new Map((modelsQuery.data ?? []).map((m) => [m.id, m.name] as const));
  }, [modelsQuery.data]);

  const providerNameById = React.useMemo(() => {
    return new Map((providersQuery.data ?? []).map((p) => [p.id, p.name] as const));
  }, [providersQuery.data]);

  return (
    <div className="space-y-6 w-full">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Model Providers</h1>
          <p className="text-sm text-muted-foreground">
            Manage which models are available through each provider.
          </p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            onClick={() => {
              modelProvidersQuery.refetch();
              modelsQuery.refetch();
              providersQuery.refetch();
            }}
            disabled={
              modelProvidersQuery.isFetching || modelsQuery.isFetching || providersQuery.isFetching
            }
          >
            Refresh
          </Button>

          <CreateModelProviderDialog modelsQuery={modelsQuery} providersQuery={providersQuery} />
        </div>
      </div>

      <ModelProviderTable
        modelProvidersQuery={modelProvidersQuery}
        modelNameById={modelNameById}
        providerNameById={providerNameById}
      />
    </div>
  );
}
