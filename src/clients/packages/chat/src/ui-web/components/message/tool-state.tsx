"use client";

import { AlertTriangle, Bot, CheckCircle2, Circle, ListChecks, Network } from "lucide-react";

import type { AiMessage } from "@agw/api";
import { Badge, cn } from "@agw/components";

type TodoItem = {
  id: string;
  title: string;
  description?: string | null;
  isComplete: boolean;
};

type BackgroundTask = {
  id: string;
  agentName: string;
  description?: string | null;
  status: string;
};

function readArray<T>(value: unknown): T[] {
  return Array.isArray(value) ? (value as T[]) : [];
}

export function isToolStateMessage(message: AiMessage): boolean {
  const type = message.additionalProperties?.type;
  return (
    type === "tool-todo-snapshot" ||
    type === "tool-mode-status" ||
    type === "tool-background-task-status" ||
    type === "tool-warning"
  );
}

export function ToolState({ message }: { message: AiMessage }) {
  const type = message.additionalProperties?.type;

  if (type === "tool-todo-snapshot") {
    const items = readArray<TodoItem>(message.additionalProperties?.items);
    return (
      <div className="mr-12 max-w-2xl rounded-xl border bg-card p-3 shadow-xs">
        <div className="mb-2 flex items-center gap-2">
          <ListChecks className="h-4 w-4 text-primary" />
          <span className="text-sm font-medium">Todo</span>
          <Badge variant="secondary" className="ml-auto">
            {items.filter((item) => item.isComplete).length}/{items.length}
          </Badge>
        </div>
        {items.length === 0 ? (
          <p className="text-xs text-muted-foreground">No todo items.</p>
        ) : (
          <div className="space-y-1.5">
            {items.map((item) => (
              <div key={item.id} className="flex items-start gap-2 rounded-md px-1 py-1">
                {item.isComplete ? (
                  <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-600" />
                ) : (
                  <Circle className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
                )}
                <div className="min-w-0">
                  <div
                    className={cn(
                      "text-sm",
                      item.isComplete && "text-muted-foreground line-through",
                    )}
                  >
                    {item.title}
                  </div>
                  {item.description ? (
                    <p className="text-xs text-muted-foreground">{item.description}</p>
                  ) : null}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    );
  }

  if (type === "tool-mode-status") {
    const mode = message.additionalProperties?.mode;
    return (
      <div className="mr-12 flex max-w-md items-center gap-2 rounded-lg border bg-card px-3 py-2 shadow-xs">
        <Network className="h-4 w-4 text-primary" />
        <span className="text-xs text-muted-foreground">Agent mode</span>
        <Badge variant="secondary" className="ml-auto capitalize">
          {typeof mode === "string" ? mode : "unknown"}
        </Badge>
      </div>
    );
  }

  if (type === "tool-background-task-status") {
    const tasks = readArray<BackgroundTask>(message.additionalProperties?.tasks);
    return (
      <div className="mr-12 max-w-2xl rounded-xl border bg-card p-3 shadow-xs">
        <div className="mb-2 flex items-center gap-2">
          <Bot className="h-4 w-4 text-primary" />
          <span className="text-sm font-medium">Background tasks</span>
          <Badge variant="secondary" className="ml-auto">
            {tasks.length} running
          </Badge>
        </div>
        {tasks.length === 0 ? (
          <p className="text-xs text-muted-foreground">No active background tasks.</p>
        ) : (
          <div className="space-y-2">
            {tasks.map((task) => (
              <div key={task.id} className="rounded-md border bg-muted/20 px-2.5 py-2">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-medium">{task.agentName}</span>
                  <Badge variant="outline" className="ml-auto capitalize">
                    {task.status}
                  </Badge>
                </div>
                {task.description ? (
                  <p className="mt-1 text-xs text-muted-foreground">{task.description}</p>
                ) : null}
              </div>
            ))}
          </div>
        )}
      </div>
    );
  }

  if (type === "tool-warning") {
    const warning = message.contents.find(
      (content) => typeof content.content === "string",
    )?.content;
    return (
      <div className="mr-12 flex max-w-2xl items-start gap-2 rounded-lg border border-amber-300/70 bg-amber-50 px-3 py-2 text-amber-950 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-100">
        <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
        <p className="text-xs leading-relaxed">
          {typeof warning === "string" ? warning : "A Tool used a fallback."}
        </p>
      </div>
    );
  }

  return null;
}

export function MessageCitations({ message }: { message: AiMessage }) {
  const citations = message.contents.flatMap((content) =>
    readArray<{ title?: string; url?: string; snippet?: string }>(
      content.additionalProperties?.citations,
    ),
  );
  const unique = citations.filter(
    (citation, index) =>
      typeof citation.url === "string" &&
      citations.findIndex((candidate) => candidate.url === citation.url) === index,
  );
  if (unique.length === 0) return null;

  return (
    <div className="mt-3 flex flex-wrap gap-1.5 border-t pt-2">
      {unique.map((citation, index) => (
        <a
          key={citation.url}
          href={citation.url}
          target="_blank"
          rel="noreferrer"
          title={citation.snippet}
          className="inline-flex max-w-full items-center gap-1 rounded-full border bg-muted/30 px-2 py-1 text-[11px] text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
        >
          <span className="shrink-0">{index + 1}</span>
          <span className="truncate">{citation.title || citation.url}</span>
        </a>
      ))}
    </div>
  );
}
