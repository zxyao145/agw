"use client";

import * as React from "react";
import { useQuery } from "@agw/components/query";

import { apiGet } from "@agw/api";

import { ProvidersHeader } from "./components/providers-header";
import { CreateProviderDialog } from "./components/create-provider-dialog";
import { ProvidersTable } from "./components/providers-table";
import type { ProviderDto } from "./components/types";

export default function ProvidersPage() {
  const [createOpen, setCreateOpen] = React.useState(false);

  const providersQuery = useQuery({
    queryKey: ["providers"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas.
      return (await apiGet("/api/providers")) as unknown as ProviderDto[];
    },
  });

  return (
    <div className="space-y-6 w-full">
      <ProvidersHeader
        onRefresh={() => providersQuery.refetch()}
        isRefreshing={providersQuery.isFetching}
        onCreateClick={() => setCreateOpen(true)}
      />

      <ProvidersTable
        providers={providersQuery.data}
        isLoading={providersQuery.isLoading}
        isError={providersQuery.isError}
        error={providersQuery.error}
      />

      <CreateProviderDialog open={createOpen} onOpenChange={setCreateOpen} />
    </div>
  );
}
