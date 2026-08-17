"use client";

import * as React from "react";
import { useQuery } from "@agw/components/query";

import { apiGet } from "@agw/api";

import { ModelsHeader } from "./components/models-header";
import { CreateModelDialog } from "./components/create-model-dialog";
import { ModelsTable } from "./components/models-table";

export default function ModelsPage() {
  const [createOpen, setCreateOpen] = React.useState(false);

  const modelsQuery = useQuery({
    queryKey: ["models"],
    queryFn: async () => {
      return (await apiGet("/api/models")) ?? [];
    },
  });

  return (
    <div className="space-y-6 w-full">
      <ModelsHeader
        onRefresh={() => modelsQuery.refetch()}
        isRefreshing={modelsQuery.isFetching}
        onCreateClick={() => setCreateOpen(true)}
      />

      <ModelsTable
        models={modelsQuery.data}
        isLoading={modelsQuery.isLoading}
        isError={modelsQuery.isError}
        error={modelsQuery.error}
      />

      <CreateModelDialog open={createOpen} onOpenChange={setCreateOpen} />
    </div>
  );
}
