"use client";

import * as React from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@agw/components/query";
import mermaid from "mermaid";
import { toast } from "sonner";

import { apiDelete, apiGet } from "@agw/api";
import { PaginatedTable } from "@agw/components";
import { Button } from "@agw/components";
import { Dialog, DialogClose, DialogContent, DialogHeader, DialogTitle } from "@agw/components";
import { cn } from "@agw/components";
import { DEFAULT_PAGE_SIZE, getClampedPageIndex, type PagedResult } from "@agw/components";
import { VisualAgentflowDialog } from "./components/visual-agentflow-dialog";
import type {
  AgentDto,
  AgentflowDetailDto,
  AgentflowDto,
  ModelProviderDto,
} from "../../../types/agentflow";
import { AgentflowsTable, ExecuteAgentflowDrawer, fetchAgentflowDetails } from "./components";
import { Copy, X } from "lucide-react";
import {
  createDefaultMermaidViewport,
  panViewport,
  zoomViewport,
  type MermaidViewportTransform,
} from "./components/mermaid-viewport";

export default function AgentflowsPage() {
  const queryClient = useQueryClient();
  const [pageIndex, setPageIndex] = React.useState(1);
  const [pageSize, setPageSize] = React.useState(DEFAULT_PAGE_SIZE);
  const [visualOpen, setVisualOpen] = React.useState(false);

  const agentflowsQuery = useQuery({
    queryKey: ["agentflows", "paged", pageIndex, pageSize],
    queryFn: async () => {
      return (await apiGet("/api/agentflows/paged", {
        params: { query: { pageIndex, pageSize } },
      })) as unknown as PagedResult<AgentflowDto>;
    },
    placeholderData: keepPreviousData,
  });

  const agentflowOptionsQuery = useQuery({
    queryKey: ["agentflows", "options"],
    queryFn: async () => (await apiGet("/api/agentflows")) as unknown as AgentflowDto[],
    enabled: visualOpen,
  });

  const total = Number(agentflowsQuery.data?.total ?? 0);

  React.useEffect(() => {
    if (!agentflowsQuery.data) return;
    const clampedPageIndex = getClampedPageIndex(total, pageIndex, pageSize);
    if (clampedPageIndex !== pageIndex) {
      setPageIndex(clampedPageIndex);
    }
  }, [agentflowsQuery.data, pageIndex, pageSize, total]);

  const agentsQuery = useQuery({
    queryKey: ["agents"],
    queryFn: async () => {
      // OpenAPI currently doesn't declare response schemas.
      return (await apiGet("/api/agents")) as unknown as AgentDto[];
    },
  });

  const modelProvidersQuery = useQuery({
    queryKey: ["modelProviders"],
    queryFn: async () => {
      return (await apiGet("/api/model-providers")) as unknown as ModelProviderDto[];
    },
  });

  const [editingAgentflow, setEditingAgentflow] = React.useState<AgentflowDetailDto | null>(null);

  // Execute drawer state
  const [executeOpen, setExecuteOpen] = React.useState(false);
  const [executingAgentflow, setExecutingAgentflow] = React.useState<AgentflowDto | null>(null);

  const [mermaidOpen, setMermaidOpen] = React.useState(false);
  const [mermaidAgentflow, setMermaidAgentflow] = React.useState<AgentflowDto | null>(null);
  const [mermaidText, setMermaidText] = React.useState("");
  const [mermaidRenderError, setMermaidRenderError] = React.useState<string | null>(null);
  const [isMermaidLoading, setIsMermaidLoading] = React.useState(false);
  const [mermaidViewport, setMermaidViewport] = React.useState<MermaidViewportTransform>(() =>
    createDefaultMermaidViewport(),
  );
  const [isMermaidDragging, setIsMermaidDragging] = React.useState(false);
  const mermaidViewportRef = React.useRef<HTMLDivElement | null>(null);
  const mermaidContainerRef = React.useRef<HTMLDivElement | null>(null);
  const mermaidDragRef = React.useRef<{
    pointerId: number;
    x: number;
    y: number;
  } | null>(null);
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
    setMermaidViewport(createDefaultMermaidViewport());
    setIsMermaidDragging(false);
    mermaidDragRef.current = null;
  }, [mermaidOpen, normalizedMermaidText]);

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
        const { svg, bindFunctions } = await mermaid.render(renderId, normalizedMermaidText);

        if (cancelled || !container) {
          return;
        }

        container.innerHTML = svg;
        bindFunctions?.(container);
      } catch (error) {
        if (!cancelled) {
          setMermaidRenderError(
            error instanceof Error ? error.message : "Failed to render Mermaid chart",
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

  const deleteAgentflowMutation = useMutation({
    mutationFn: async (id: string) => {
      return await apiDelete("/api/agentflows/{id}", {
        params: { path: { id } },
      });
    },
    onSuccess: async () => {
      toast.success("Agentflow deleted");
      setPageIndex(getClampedPageIndex(Math.max(0, total - 1), pageIndex, pageSize));
      await queryClient.invalidateQueries({ queryKey: ["agentflows"] });
    },
    onError: (error) => {
      toast.error(`Delete failed: ${error.message}`);
    },
  });

  const handleDelete = React.useCallback(
    (agentflow: AgentflowDto) => {
      if (window.confirm(`Are you sure you want to delete "${agentflow.name}"?`)) {
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
    setPageIndex(1);
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

  const handleViewMermaid = React.useCallback(async (agentflow: AgentflowDto) => {
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

      setMermaidText(typeof result === "string" ? result : JSON.stringify(result, null, 2));
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
  }, []);

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

  const handleMermaidWheel = React.useCallback((event: WheelEvent) => {
    const viewport = mermaidViewportRef.current;

    if (!viewport) {
      return;
    }

    event.preventDefault();

    const rect = viewport.getBoundingClientRect();
    setMermaidViewport((current) =>
      zoomViewport({
        viewport: current,
        cursor: {
          x: event.clientX - rect.left,
          y: event.clientY - rect.top,
        },
        deltaY: event.deltaY,
        deltaMode: event.deltaMode,
      }),
    );
  }, []);

  React.useEffect(() => {
    const viewport = mermaidViewportRef.current;

    if (!viewport) {
      return;
    }

    viewport.addEventListener("wheel", handleMermaidWheel, { passive: false });
    return () => viewport.removeEventListener("wheel", handleMermaidWheel);
  }, [handleMermaidWheel, isMermaidLoading, mermaidOpen, normalizedMermaidText]);

  const handleMermaidPointerDown = React.useCallback(
    (event: React.PointerEvent<HTMLDivElement>) => {
      if (event.button !== 0) {
        return;
      }

      event.preventDefault();
      event.currentTarget.setPointerCapture(event.pointerId);
      mermaidDragRef.current = {
        pointerId: event.pointerId,
        x: event.clientX,
        y: event.clientY,
      };
      setIsMermaidDragging(true);
    },
    [],
  );

  const handleMermaidPointerMove = React.useCallback(
    (event: React.PointerEvent<HTMLDivElement>) => {
      const drag = mermaidDragRef.current;

      if (!drag || drag.pointerId !== event.pointerId) {
        return;
      }

      event.preventDefault();

      const movement = {
        x: event.clientX - drag.x,
        y: event.clientY - drag.y,
      };

      mermaidDragRef.current = {
        ...drag,
        x: event.clientX,
        y: event.clientY,
      };
      setMermaidViewport((current) => panViewport({ viewport: current, movement }));
    },
    [],
  );

  const handleMermaidPointerEnd = React.useCallback((event: React.PointerEvent<HTMLDivElement>) => {
    const drag = mermaidDragRef.current;

    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }

    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }

    mermaidDragRef.current = null;
    setIsMermaidDragging(false);
  }, []);

  return (
    <div className="space-y-6 w-full">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">Agentflows</h1>
          <p className="text-sm text-muted-foreground">Manage agentflows and execute them.</p>
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

          <Button className="cursor-pointer" onClick={() => setVisualOpen(true)}>
            Create
          </Button>

          <VisualAgentflowDialog
            open={visualOpen}
            onOpenChange={handleVisualDialogClose}
            agents={agentsQuery.data || []}
            agentflows={agentflowOptionsQuery.data || []}
            modelProviders={modelProvidersQuery.data || []}
            editingAgentflow={editingAgentflow}
            onAgentflowCreated={handleAgentflowCreated}
          />
        </div>
      </div>

      <PaginatedTable
        pageIndex={pageIndex}
        pageSize={pageSize}
        total={total}
        isFetching={agentflowsQuery.isFetching}
        onPageIndexChange={setPageIndex}
        onPageSizeChange={(value) => {
          setPageSize(value);
          setPageIndex(1);
        }}
      >
        <AgentflowsTable
          embedded
          agentflows={agentflowsQuery.data?.items || []}
          isLoading={agentflowsQuery.isLoading}
          isError={agentflowsQuery.isError}
          error={agentflowsQuery.error}
          deleteMutation={deleteAgentflowMutation}
          onEdit={handleEdit}
          onDelete={handleDelete}
          onExecute={handleExecute}
          onViewMermaid={handleViewMermaid}
        />
      </PaginatedTable>

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

          <div className="relative flex h-full min-h-0 flex-col overflow-hidden rounded-md border bg-muted/30 p-4">
            {isMermaidLoading ? (
              <p className="text-sm text-muted-foreground">Loading Mermaid text...</p>
            ) : (
              <>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="absolute top-3 right-3 z-10"
                  onClick={handleCopyMermaid}
                  disabled={isMermaidLoading || !mermaidText.trim()}
                >
                  <Copy />
                </Button>
                {normalizedMermaidText ? (
                  <div
                    ref={mermaidViewportRef}
                    className={cn(
                      "relative min-h-0 flex-1 overflow-hidden rounded-sm touch-none select-none",
                      isMermaidDragging ? "cursor-grabbing" : "cursor-grab",
                    )}
                    onPointerDown={handleMermaidPointerDown}
                    onPointerMove={handleMermaidPointerMove}
                    onPointerUp={handleMermaidPointerEnd}
                    onPointerCancel={handleMermaidPointerEnd}
                    onLostPointerCapture={handleMermaidPointerEnd}
                  >
                    <div
                      className="flex h-full w-full items-center justify-center px-4 will-change-transform"
                      ref={mermaidContainerRef}
                      style={{
                        transform: `translate(${mermaidViewport.x}px, ${mermaidViewport.y}px) scale(${mermaidViewport.scale})`,
                        transformOrigin: "0 0",
                      }}
                    />
                  </div>
                ) : (
                  <p className="text-sm text-muted-foreground">No Mermaid content returned.</p>
                )}
              </>
            )}

            {mermaidRenderError ? (
              <pre className="mt-3 max-h-32 overflow-auto rounded-md border bg-background/80 p-3 text-xs whitespace-pre-wrap">
                Failed to render Mermaid: {mermaidRenderError}
              </pre>
            ) : null}
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
