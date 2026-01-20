"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import { apiGet, apiPut, apiDelete } from "@/api/client";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { VisualAgentflowDialog } from "./components/visual-agentflow-dialog";
import {
  AgentDto,
  AgentflowDto,
  AgentflowDetailDto
} from "@/types/agentflow";
import {
  AgentflowsTable,
  ExecuteAgentflowDrawer,
  fetchAgentflowDetails,
} from "./components";

export default function AgentflowsPage() {
  const queryClient = useQueryClient();

  const agentflowsQuery = useQuery({
    queryKey: ["agentflows"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas.
      return (await apiGet("/api/agentflows")) as unknown as AgentflowDto[];
    },
  });

  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas.
      return (await apiGet("/api/agents")) as unknown as AgentDto[];
    },
  });

  const [visualOpen, setVisualOpen] = React.useState(false);
  const [editingAgentflow, setEditingAgentflow] = React.useState<AgentflowDetailDto | null>(null);

  // Execute drawer state
  const [executeOpen, setExecuteOpen] = React.useState(false);
  const [executingAgentflow, setExecutingAgentflow] =
    React.useState<AgentflowDto | null>(null);

  const updateAgentflowMutation = useMutation({
    mutationFn: async ({ id, body }: { id: string; body: AgentflowDetailDto }) => {
      return await apiPut("/api/agentflows/{id}", {
        params: { path: { id } },
        body,
      });
    },
    onSuccess: async () => {
      toast.success("Agentflow updated");
      await queryClient.invalidateQueries({ queryKey: ["agentflows"] });
    },
    onError: (error) => {
      toast.error(`Update failed: ${error.message}`);
    },
  });

  const deleteAgentflowMutation = useMutation({
    mutationFn: async (id: string) => {
      // @ts-expect-error - OpenAPI schema has incorrect top-level path parameters definition
      return await apiDelete("/api/agentflows/{id}", {
        params: { path: { id } },
      });
    },
    onSuccess: async () => {
      toast.success("Agentflow deleted");
      await queryClient.invalidateQueries({ queryKey: ["agentflows"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${error.message}`);
    },
  });

  const handleToggleEnabled = React.useCallback(
    async (agentflow: AgentflowDto) => {
      try {
        const details = await fetchAgentflowDetails(agentflow.id);

        updateAgentflowMutation.mutate({
          id: agentflow.id,
          body: {
            ...agentflow,
            ...details,
            enable: !agentflow.enable,
          },
        });
      } catch (error) {
        toast.error("Failed to fetch agentflow details");
      }
    },
    [updateAgentflowMutation]
  );

  const handleDelete = React.useCallback(
    (agentflow: AgentflowDto) => {
      if (
        window.confirm(`Are you sure you want to delete "${agentflow.name}"?`)
      ) {
        deleteAgentflowMutation.mutate(agentflow.id);
      }
    },
    [deleteAgentflowMutation]
  );

  const handleEdit = React.useCallback(async (agentflow: AgentflowDto) => {
    try {
      const details = await fetchAgentflowDetails(agentflow.id);

      setEditingAgentflow({
        ...agentflow,
        ...details,
      });

      setVisualOpen(true);
    } catch (error) {
      toast.error("Failed to load agentflow details");
      console.error("Failed to load agentflow:", error);
    }
  }, []);

  const handleAgentflowCreated = React.useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: ["agentflows"] });
    setVisualOpen(false);
    setEditingAgentflow(null);
  }, [queryClient]);

  const handleVisualDialogClose = React.useCallback(() => {
    setVisualOpen(false);
    setEditingAgentflow(null);
  }, []);

  const handleExecute = React.useCallback((agentflow: AgentflowDto) => {
    setExecutingAgentflow(agentflow);
    setExecuteOpen(true);
  }, []);

  return (
    <div className="space-y-6 w-full">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Agentflows</h1>
          <p className="text-sm text-muted-foreground">
            Manage agentflows and execute them.
          </p>
        </div>

        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Button
            variant="outline"
            className="cursor-pointer"
            onClick={() => agentflowsQuery.refetch()}
            disabled={agentflowsQuery.isFetching}
          >
            Refresh
          </Button>

          <Button
            className="cursor-pointer"
            onClick={() => setVisualOpen(true)}
          >
            Create Agentflow
          </Button>

          <VisualAgentflowDialog
            open={visualOpen}
            onOpenChange={handleVisualDialogClose}
            agents={agentsQuery.data || []}
            agentflows={agentflowsQuery.data || []}
            editingAgentflow={editingAgentflow}
            onAgentflowCreated={handleAgentflowCreated}
          />
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Agentflows</CardTitle>
          <CardDescription>
            Fetched from <code>/api/agentflows</code>.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <AgentflowsTable
            agentflows={agentflowsQuery.data || []}
            isLoading={agentflowsQuery.isLoading}
            isError={agentflowsQuery.isError}
            error={agentflowsQuery.error}
            updateMutation={updateAgentflowMutation}
            deleteMutation={deleteAgentflowMutation}
            onToggleEnabled={handleToggleEnabled}
            onEdit={handleEdit}
            onDelete={handleDelete}
            onExecute={handleExecute}
          />
        </CardContent>
      </Card>

      <ExecuteAgentflowDrawer
        open={executeOpen}
        onOpenChange={setExecuteOpen}
        agentflow={executingAgentflow}
      />
    </div>
  );
}
