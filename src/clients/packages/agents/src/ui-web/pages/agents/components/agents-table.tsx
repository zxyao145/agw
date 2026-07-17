import { UseQueryResult } from "@agw/components/query";
import { Button } from "@agw/components";
import { ButtonGroup } from "@agw/components";
import { TableBody, TableCell, TableHead, TableHeader, TableRow } from "@agw/components";
import { Pencil, Trash2, Play } from "lucide-react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@agw/components";
import type { AgentDto } from "./types";
import { getApiErrorMessage } from "@agw/api";
import { StaticTable } from "@agw/components";
import { Empty } from "@agw/components";
import { formatLocalDateTime } from "@agw/components";
import type { PagedResult } from "@agw/components";

interface AgentsTableProps {
  embedded?: boolean;
  agentsQuery: UseQueryResult<PagedResult<AgentDto>, Error>;
  onEdit: (agent: AgentDto) => void;
  onDelete: (agent: AgentDto) => void;
  onExecute: (agent: AgentDto) => void;
}

export function AgentsTable({
  embedded = false,
  agentsQuery,
  onEdit,
  onDelete,
  onExecute,
}: AgentsTableProps) {
  const agents = agentsQuery.data?.items ?? [];

  if (agentsQuery.isLoading) {
    return <div className="text-sm text-muted-foreground">Loading...</div>;
  }
  if (agentsQuery.isError) {
    return (
      <div className="text-sm text-destructive">
        Failed to load agents: {getApiErrorMessage(agentsQuery.error)}
      </div>
    );
  }

  return (
    <StaticTable embedded={embedded} isEmpty={agents.length === 0}>
      <Empty>
        <div className="text-sm text-muted-foreground">
          No agents found. Create one to get started.
        </div>
      </Empty>

      <TableHeader>
        <TableRow>
          <TableHead>Name</TableHead>
          <TableHead className="min-w-40">Display Name</TableHead>
          <TableHead>Type</TableHead>
          <TableHead>Description</TableHead>
          <TableHead>Instructions</TableHead>
          <TableHead>Tools</TableHead>
          <TableHead>Updated</TableHead>
          <TableHead className="text-right">Actions</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {agents.map((agent) => {
          let toolNames: string[] = [];
          try {
            toolNames = agent.tools ? JSON.parse(agent.tools) : [];
          } catch {
            toolNames = [];
          }

          return (
            <TableRow key={agent.id}>
              <TableCell>
                <div className="font-medium">{agent.name}</div>
                <div className="font-mono text-xs break-all text-muted-foreground">{agent.id}</div>
              </TableCell>
              <TableCell className="font-medium">{agent.displayName}</TableCell>
              <TableCell>
                <span
                  className={`text-xs px-2 py-1 rounded-full ${
                    agent.type === 0
                      ? "bg-blue-100 text-blue-700 dark:bg-blue-900 dark:text-blue-300"
                      : "bg-green-100 text-green-700 dark:bg-green-900 dark:text-green-300"
                  }`}
                >
                  {agent.type === 0 ? "System" : "External"}
                </span>
              </TableCell>
              <TableCell className="max-w-xs truncate">{agent.description || "-"}</TableCell>
              <TableCell className="max-w-xs">
                {agent.systemPrompt ? (
                  <Tooltip>
                    <TooltipTrigger asChild>
                      <span className="block truncate text-xs" tabIndex={0}>
                        {agent.systemPrompt}
                      </span>
                    </TooltipTrigger>
                    <TooltipContent
                      side="top"
                      className="max-h-80 max-w-[min(40rem,calc(100vw-2rem))] overflow-y-auto whitespace-pre-wrap break-words text-left"
                    >
                      {agent.systemPrompt}
                    </TooltipContent>
                  </Tooltip>
                ) : (
                  <span className="text-muted-foreground">-</span>
                )}
              </TableCell>
              <TableCell className="max-w-xs">
                {toolNames.length > 0 ? (
                  <span className="text-xs">
                    {toolNames.slice(0, 2).join(", ")}
                    {toolNames.length > 2 && ` +${toolNames.length - 2} more`}
                  </span>
                ) : (
                  <span className="text-muted-foreground">-</span>
                )}
              </TableCell>
              <TableCell className="text-xs text-muted-foreground">
                {formatLocalDateTime(agent.updateTime ?? agent.createTime)}
              </TableCell>
              <TableCell className="text-right">
                <div className="flex justify-end gap-2">
                  <ButtonGroup>
                    <Button
                      variant="ghost"
                      className="cursor-pointer"
                      size="sm"
                      onClick={() => onExecute(agent)}
                    >
                      <Play className="w-4 h-4" />
                    </Button>
                    <Button
                      variant="ghost"
                      className="cursor-pointer"
                      size="sm"
                      onClick={() => onEdit(agent)}
                    >
                      <Pencil className="w-4 h-4" />
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => onDelete(agent)}
                      className="cursor-pointer text-destructive hover:text-destructive hover:bg-destructive/10"
                    >
                      <Trash2 className="w-4 h-4" />
                    </Button>
                  </ButtonGroup>
                </div>
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </StaticTable>
  );
}
