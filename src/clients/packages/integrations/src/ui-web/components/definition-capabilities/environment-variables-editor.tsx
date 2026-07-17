import { Plus, Trash2 } from "lucide-react";

import { Button } from "@agw/components";
import { Input } from "@agw/components";

import {
  getEnvironmentVariablesError,
  type EnvironmentVariableEntry,
} from "./environment-variables";

interface EnvironmentVariablesEditorProps {
  entries: EnvironmentVariableEntry[];
  setEntries: (entries: EnvironmentVariableEntry[]) => void;
  idPrefix?: string;
  scopeLabel?: string;
}

export function EnvironmentVariablesEditor({
  entries,
  setEntries,
  idPrefix = "",
  scopeLabel = "agent",
}: EnvironmentVariablesEditorProps) {
  const error = getEnvironmentVariablesError(entries);

  const updateEntry = (index: number, field: keyof EnvironmentVariableEntry, value: string) => {
    setEntries(
      entries.map((entry, entryIndex) =>
        entryIndex === index ? { ...entry, [field]: value } : entry,
      ),
    );
  };

  return (
    <div className="space-y-6">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h3 className="font-medium">Environment Variables</h3>
          <p className="mt-1 text-sm text-muted-foreground">
            Configure variables scoped to this {scopeLabel} and the processes it starts.
          </p>
        </div>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => setEntries([...entries, { key: "", value: "" }])}
        >
          <Plus />
          Add Variable
        </Button>
      </div>

      {entries.length === 0 ? (
        <div className="rounded-lg border border-dashed bg-muted/20 px-4 py-8 text-center text-sm text-muted-foreground">
          No environment variables configured
        </div>
      ) : (
        <div className="overflow-hidden rounded-lg border">
          <div className="grid grid-cols-[minmax(0,2fr)_minmax(0,3fr)_40px] gap-3 border-b bg-muted/40 px-3 py-2 text-xs font-medium text-muted-foreground">
            <div>Key</div>
            <div>Value</div>
            <span className="sr-only">Actions</span>
          </div>
          {entries.map((entry, index) => (
            <div
              key={`${idPrefix}environment-variable-${index}`}
              className="grid grid-cols-[minmax(0,2fr)_minmax(0,3fr)_40px] gap-3 border-b p-3 last:border-b-0"
            >
              <Input
                id={`${idPrefix}environment-variable-key-${index}`}
                value={entry.key}
                onChange={(event) => updateEntry(index, "key", event.target.value)}
                placeholder="VARIABLE_NAME"
                aria-label={`Environment variable ${index + 1} key`}
                aria-invalid={Boolean(error)}
                className="font-mono text-xs"
              />
              <Input
                id={`${idPrefix}environment-variable-value-${index}`}
                value={entry.value}
                onChange={(event) => updateEntry(index, "value", event.target.value)}
                placeholder="value"
                aria-label={`Environment variable ${index + 1} value`}
                className="font-mono text-xs"
              />
              <Button
                type="button"
                variant="ghost"
                size="icon"
                aria-label={`Remove environment variable ${index + 1}`}
                onClick={() => setEntries(entries.filter((_, entryIndex) => entryIndex !== index))}
              >
                <Trash2 />
              </Button>
            </div>
          ))}
        </div>
      )}

      {error ? (
        <p className="text-sm text-destructive" role="alert">
          {error}
        </p>
      ) : (
        <p className="text-sm text-muted-foreground">
          Values are stored and displayed as plain text, matching MCP Tool Server settings.
        </p>
      )}
    </div>
  );
}
