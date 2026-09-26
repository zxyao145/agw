import {
  ArrowDownToLine,
  ArrowUpFromLine,
  Brain,
  CheckCircle2,
  Circle,
  CircleGauge,
  Database,
  ListChecks,
} from "lucide-react";

import { formatTokenCount, type TokenUsage } from "@agw/api";
import type { TodoPresentationItem } from "@agw/chat-core";
import { Badge, cn, Tooltip, TooltipContent, TooltipTrigger } from "@agw/components";

export interface ChatAsideProps {
  usage: TokenUsage;
  todos: readonly TodoPresentationItem[];
}

export function ChatAside({ usage, todos }: ChatAsideProps) {
  return (
    <aside
      className="sticky top-0 right-2 hidden w-75 shrink-0 self-start border-border/60 bg-background py-10 @min-[64rem]:block"
      aria-label="Current conversation details"
    >
      <div className="space-y-2 rounded-2xl border border-border bg-background/50 px-3 py-3 shadow-xs">
        <h2 className="mb-2 text-base font-medium text-muted-foreground">Token usage</h2>
        <dl className="space-y-1.5">
          <div className="session-aside-row">
            <CircleGauge className="size-4 shrink-0" aria-hidden="true" />
            <dt className="text-sm font-medium text-foreground">Total</dt>
            <dd className="ml-auto font-mono text-sm font-medium tabular-nums">
              {formatTokenCount(usage.totalTokenCount)}
            </dd>
          </div>
          <div className="session-aside-row">
            <ArrowDownToLine className="size-4 shrink-0" aria-hidden="true" />
            <dt className="text-sm text-foreground">Input</dt>
            <dd className="ml-auto font-mono text-sm tabular-nums text-foreground/80">
              {formatTokenCount(usage.inputTokenCount)}
            </dd>
          </div>
          <div className="session-aside-row">
            <ArrowUpFromLine className="size-4 shrink-0" aria-hidden="true" />
            <dt className="text-sm text-foreground">Output</dt>
            <dd className="ml-auto font-mono text-sm tabular-nums text-foreground/80">
              {formatTokenCount(usage.outputTokenCount)}
            </dd>
          </div>
          <div className="session-aside-row">
            <Database className="size-4 shrink-0" aria-hidden="true" />
            <dt className="text-sm text-foreground">Cached input</dt>
            <dd className="ml-auto font-mono text-sm tabular-nums text-foreground/80">
              {formatTokenCount(usage.cachedInputTokenCount)}
            </dd>
          </div>
          <div className="session-aside-row">
            <Brain className="size-4 shrink-0" aria-hidden="true" />
            <dt className="text-sm text-foreground">Reasoning</dt>
            <dd className="ml-auto font-mono text-sm tabular-nums text-foreground/80">
              {formatTokenCount(usage.reasoningTokenCount)}
            </dd>
          </div>
        </dl>
      </div>
      {todos.length > 0 ? (
        <section
          className="mt-4 rounded-2xl border border-border bg-background/50 px-3 py-3 shadow-xs"
          aria-label="Current turn todos"
        >
          <div className="mb-2 flex items-center gap-2">
            <ListChecks className="size-4 text-primary" aria-hidden="true" />
            <h2 className="text-base font-medium text-muted-foreground">Todo</h2>
            <Badge variant="secondary" className="ml-auto">
              {todos.filter((todo) => todo.isComplete).length}/{todos.length}
            </Badge>
          </div>
          <ul className="space-y-2">
            {todos.map((todo) => (
              <li key={todo.id} className="flex items-start gap-2 rounded-lg px-1 py-1">
                {todo.isComplete ? (
                  <CheckCircle2
                    className="mt-2 size-4 shrink-0 text-emerald-600"
                    aria-hidden="true"
                  />
                ) : (
                  <Circle
                    className="mt-2 size-4 shrink-0 text-muted-foreground"
                    aria-hidden="true"
                  />
                )}
                <div className="min-w-0">
                  {todo.description ? (
                    <Tooltip>
                      <TooltipTrigger asChild>
                        <span
                          tabIndex={0}
                          className={cn(
                            "wrap-break-word text-sm",
                            todo.isComplete && "text-muted-foreground line-through",
                          )}
                        >
                          {todo.title}
                        </span>
                      </TooltipTrigger>
                      <TooltipContent
                        side="left"
                        className="max-h-80 max-w-[min(20rem,calc(100vw-2rem))] overflow-x-hidden overflow-y-auto whitespace-pre-wrap break-words text-left"
                      >
                        {todo.description}
                      </TooltipContent>
                    </Tooltip>
                  ) : (
                    <div
                      className={cn(
                        "wrap-break-word text-sm",
                        todo.isComplete && "text-muted-foreground line-through",
                      )}
                    >
                      {todo.title}
                    </div>
                  )}
                </div>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </aside>
  );
}
