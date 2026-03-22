"use client";

import * as React from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import mermaid from "mermaid";
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
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { VisualAgentflowDialog } from "./components/visual-agentflow-dialog";
import { AgentDto, AgentflowDto, AgentflowDetailDto } from "@/types/agentflow";
import {
  AgentflowsTable,
  ExecuteAgentflowDrawer,
  fetchAgentflowDetails,
} from "./components";
import { Copy, X } from "lucide-react";

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
  const [editingAgentflow, setEditingAgentflow] =
    React.useState<AgentflowDetailDto | null>(null);

  // Execute drawer state
  const [executeOpen, setExecuteOpen] = React.useState(false);
  const [executingAgentflow, setExecutingAgentflow] =
    React.useState<AgentflowDto | null>(null);

  const [mermaidOpen, setMermaidOpen] = React.useState(false);
  const [mermaidAgentflow, setMermaidAgentflow] =
    React.useState<AgentflowDto | null>(null);
  const [mermaidText, setMermaidText] = React.useState("");
  const [mermaidRenderError, setMermaidRenderError] = React.useState<
    string | null
  >(null);
  const [isMermaidLoading, setIsMermaidLoading] = React.useState(false);
  const mermaidContainerRef = React.useRef<HTMLDivElement | null>(null);
  const mermaidInitializedRef = React.useRef(false);
  const mermaidRequestIdRef = React.useRef(0);

  const normalizedMermaidText = React.useMemo(() => {
    const trimmed = mermaidText.trim();
    const lines = trimmed.split("\n");

    if (
      lines.length >= 2 &&
      lines[0].trim().startsWith("```") &&
      lines[lines.length - 1].trim() === "```"
    ) {
      return lines.slice(1, -1).join("\n").trim();
    }

    return trimmed;
  }, [mermaidText]);

  React.useEffect(() => {
    if (mermaidInitializedRef.current) {
      return;
    }

    mermaid.initialize({
      startOnLoad: false,
      securityLevel: "loose",
    });
    mermaidInitializedRef.current = true;
  }, []);

  React.useEffect(() => {
    let cancelled = false;

    const renderMermaid = async () => {
      if (
        isMermaidLoading ||
        !mermaidOpen ||
        !normalizedMermaidText ||
        !mermaidContainerRef.current
      ) {
        if (mermaidContainerRef.current) {
          mermaidContainerRef.current.innerHTML = "";
        }
        return;
      }

      try {
        const container = mermaidContainerRef.current;
        setMermaidRenderError(null);
        const renderId = `agentflow-mermaid-${crypto.randomUUID()}`;
        const { svg, bindFunctions } = await mermaid.render(
          renderId,
          normalizedMermaidText,
        );

        if (cancelled || !container) {
          return;
        }

        container.innerHTML = svg;
        bindFunctions?.(container);
      } catch (error) {
        if (!cancelled) {
          setMermaidRenderError(
            error instanceof Error
              ? error.message
              : "Failed to render Mermaid chart",
          );
          if (mermaidContainerRef.current) {
            mermaidContainerRef.current.innerHTML = "";
          }
        }
      }
    };

    void renderMermaid();

    return () => {
      cancelled = true;
    };
  }, [isMermaidLoading, mermaidOpen, normalizedMermaidText]);

  const updateAgentflowMutation = useMutation({
    mutationFn: async ({
      id,
      body,
    }: {
      id: string;
      body: AgentflowDetailDto;
    }) => {
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
      } catch {
        toast.error("Failed to fetch agentflow details");
      }
    },
    [updateAgentflowMutation],
  );

  const handleDelete = React.useCallback(
    (agentflow: AgentflowDto) => {
      if (
        window.confirm(`Are you sure you want to delete "${agentflow.name}"?`)
      ) {
        deleteAgentflowMutation.mutate(agentflow.id);
      }
    },
    [deleteAgentflowMutation],
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

  const handleViewMermaid = React.useCallback(
    async (agentflow: AgentflowDto) => {
      const requestId = mermaidRequestIdRef.current + 1;
      mermaidRequestIdRef.current = requestId;

      setMermaidAgentflow(agentflow);
      setMermaidText("");
      setMermaidRenderError(null);
      setMermaidOpen(true);
      setIsMermaidLoading(true);

      try {
        const result = await apiGet("/api/agentflows/mermaid/{id}", {
          params: { path: { id: agentflow.id } },
        });

        if (mermaidRequestIdRef.current !== requestId) {
          return;
        }

        setMermaidText(
          typeof result === "string" ? result : JSON.stringify(result, null, 2),
        );
      } catch {
        if (mermaidRequestIdRef.current !== requestId) {
          return;
        }

        toast.error("Failed to load Mermaid chart text");
        setMermaidText("");
      } finally {
        if (mermaidRequestIdRef.current === requestId) {
          setIsMermaidLoading(false);
        }
      }
    },
    [],
  );

  const handleCopyMermaid = React.useCallback(async () => {
    const textToCopy = mermaidText.trim();

    if (!textToCopy) {
      toast.error("No Mermaid text to copy");
      return;
    }

    try {
      await navigator.clipboard.writeText(textToCopy);
      toast.success("Mermaid text copied");
    } catch {
      toast.error("Failed to copy Mermaid text");
    }
  }, [mermaidText]);

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
            Create
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
        onViewMermaid={handleViewMermaid}
      />

      <ExecuteAgentflowDrawer
        open={executeOpen}
        onOpenChange={setExecuteOpen}
        agentflow={executingAgentflow}
      />

      <Dialog open={mermaidOpen} onOpenChange={setMermaidOpen}>
        <DialogContent
          className="p-4 gap-4 w-[70vw] sm:max-w-[70vw] h-[70vh] sm:max-h-[80vh] flex flex-col"
          showCloseButton={false}
        >
          <DialogHeader className="gap-0 flex flex-row align-center justify-between">
            <DialogTitle className="flex items-center">
              Mermaid Chart
              {mermaidAgentflow ? ` - ${mermaidAgentflow.name}` : ""}
            </DialogTitle>
            <DialogClose asChild>
              <Button variant="outline" size="sm" className="cursor-pointer">
                <X />
              </Button>
            </DialogClose>
          </DialogHeader>

          <div className="relative overflow-auto rounded-md border bg-muted/30 p-4 h-full">
            {isMermaidLoading ? (
              <p className="text-sm text-muted-foreground">
                Loading Mermaid text...
              </p>
            ) : (
              <>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="absolute top-3 right-3"
                  onClick={handleCopyMermaid}
                  disabled={isMermaidLoading || !mermaidText.trim()}
                >
                  <Copy />
                </Button>
                {normalizedMermaidText ? (
                  <div
                    className="px-4 flex justify-center items-center h-full"
                    ref={mermaidContainerRef}
                  />
                ) : (
                  <p className="text-sm text-muted-foreground">
                    No Mermaid content returned.
                  </p>
                )}
              </>
            )}

            {mermaidRenderError ? (
              <pre className="mt-3 rounded-md border bg-background/80 p-3 text-xs whitespace-pre-wrap">
                Failed to render Mermaid: {mermaidRenderError}
              </pre>
            ) : null}
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
