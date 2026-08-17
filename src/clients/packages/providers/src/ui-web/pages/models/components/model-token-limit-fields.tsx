import { Input, Label } from "@agw/components";

import { getModelTokenLimitError } from "./types";

interface ModelTokenLimitFieldsProps {
  idPrefix: string;
  maxContextWindowTokens: string;
  maxOutputTokens: string;
  onMaxContextWindowTokensChange: (value: string) => void;
  onMaxOutputTokensChange: (value: string) => void;
}

const tokenNumberFormatter = new Intl.NumberFormat("en-US");

export function ModelTokenLimitFields({
  idPrefix,
  maxContextWindowTokens,
  maxOutputTokens,
  onMaxContextWindowTokensChange,
  onMaxOutputTokensChange,
}: ModelTokenLimitFieldsProps) {
  const contextWindow = Number(maxContextWindowTokens);
  const maximumOutput = Number(maxOutputTokens);
  const error = getModelTokenLimitError(contextWindow, maximumOutput);
  const inputBudget = error === null ? contextWindow - maximumOutput : null;
  const outputShare = error === null ? (maximumOutput / contextWindow) * 100 : 0;

  return (
    <section className="grid gap-4 rounded-lg border bg-muted/20 p-4">
      <div>
        <h3 className="text-sm font-medium">Token limits</h3>
        <p className="mt-1 text-xs leading-5 text-muted-foreground">
          The output reserve is subtracted from the context window before automatic compaction.
        </p>
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        <div className="grid gap-2">
          <Label htmlFor={`${idPrefix}max-context-window-tokens`}>Context window</Label>
          <Input
            id={`${idPrefix}max-context-window-tokens`}
            type="number"
            min={1}
            step={1000}
            inputMode="numeric"
            value={maxContextWindowTokens}
            onChange={(event) => onMaxContextWindowTokensChange(event.target.value)}
          />
          <p className="text-xs text-muted-foreground">Maximum tokens accepted by the model.</p>
        </div>

        <div className="grid gap-2">
          <Label htmlFor={`${idPrefix}max-output-tokens`}>Maximum output</Label>
          <Input
            id={`${idPrefix}max-output-tokens`}
            type="number"
            min={1}
            step={1000}
            inputMode="numeric"
            value={maxOutputTokens}
            onChange={(event) => onMaxOutputTokensChange(event.target.value)}
          />
          <p className="text-xs text-muted-foreground">Reserved for each model response.</p>
        </div>
      </div>

      {error ? (
        <p className="text-xs font-medium text-destructive" role="alert">
          {error}
        </p>
      ) : (
        <div className="grid gap-2">
          <div className="flex items-center justify-between gap-4 text-xs">
            <span className="text-muted-foreground">Effective input budget</span>
            <span className="font-medium tabular-nums">
              {tokenNumberFormatter.format(inputBudget!)} tokens
            </span>
          </div>
          <div className="flex h-1.5 overflow-hidden rounded-full bg-muted" aria-hidden="true">
            <div className="bg-primary/70" style={{ width: `${100 - outputShare}%` }} />
            <div className="bg-primary/25" style={{ width: `${outputShare}%` }} />
          </div>
          <p className="text-xs leading-5 text-muted-foreground">
            Old tool results are compacted at 50% of this budget; old messages are truncated at 80%.
          </p>
        </div>
      )}
    </section>
  );
}
