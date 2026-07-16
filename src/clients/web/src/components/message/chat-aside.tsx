import { ArrowDownToLine, ArrowUpFromLine, Brain, CircleGauge, Database } from "lucide-react";

import { formatTokenCount, type TokenUsage } from "@/lib/token-usage";

export interface ChatAsideProps {
  usage: TokenUsage;
}

export function ChatAside({ usage }: ChatAsideProps) {
  return (
    <aside
      className="sticky top-0 hidden w-75 shrink-0 self-start border-border/60 bg-background py-10 @min-[64rem]:block"
      aria-label="Current conversation token usage"
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
    </aside>
  );
}
