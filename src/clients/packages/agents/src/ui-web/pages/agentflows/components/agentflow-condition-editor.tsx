"use client";

import * as React from "react";
import { Button, Input } from "@agw/components";
import { Plus, Trash2 } from "lucide-react";

import type { AgentflowConditionRule } from "../../../../types/agentflow";
import {
  addConditionRule,
  getAvailableConditionFields,
  removeConditionRule,
  updateConditionRule,
  type AgentflowConditionField,
} from "./agentflow-condition";

type AgentflowConditionEditorProps = {
  rules: AgentflowConditionRule[];
  sourceProducesResult: boolean;
  responseSchema?: string | null;
  onChange(rules: AgentflowConditionRule[], textField?: string): void;
  onCommitText(): void;
};

const CONDITION_LABELS: Record<AgentflowConditionField, string> = {
  contains: "Contains",
  notContains: "Does not contain",
  equals: "Equals",
  author: "Author is",
  role: "Role is",
  minMessages: "At least N messages",
  result: "Result field",
};

export function AgentflowConditionEditor({
  rules,
  sourceProducesResult,
  responseSchema,
  onChange,
  onCommitText,
}: AgentflowConditionEditorProps) {
  const fields = getAvailableConditionFields(rules, sourceProducesResult);
  const responseFields = readResponseFields(responseSchema);

  return (
    <div className="space-y-2">
      {rules.map((rule, index) => {
        const availableFields = [...fields, ...(fields.includes(rule.field) ? [] : [rule.field])];
        return (
          <div key={`${index}-${rule.field}`} className="flex items-start gap-1.5">
            <select
              aria-label="Condition field"
              className="h-8 min-w-0 flex-1 rounded-md border border-input bg-background px-2 text-xs"
              value={rule.field}
              onChange={(event) =>
                onChange(
                  updateConditionRule(rules, index, {
                    field: event.target.value as AgentflowConditionField,
                  }),
                )
              }
            >
              {availableFields.map((field) => (
                <option key={field} value={field}>
                  {CONDITION_LABELS[field] ?? field}
                </option>
              ))}
            </select>
            {rule.field === "result" ? (
              <>
                <Input
                  aria-label="Result path"
                  list={`result-paths-${index}`}
                  value={rule.path}
                  onChange={(event) =>
                    onChange(
                      updateConditionRule(rules, index, { path: event.target.value }),
                      `path-${index}`,
                    )
                  }
                  onBlur={onCommitText}
                  placeholder="Path"
                  className="h-8 min-w-0 flex-1"
                />
                <datalist id={`result-paths-${index}`}>
                  {responseFields.map((field) => (
                    <option key={field} value={field} />
                  ))}
                </datalist>
              </>
            ) : null}
            <Input
              aria-label="Condition value"
              type={rule.field === "minMessages" ? "number" : "text"}
              min={rule.field === "minMessages" ? 1 : undefined}
              value={rule.value}
              onChange={(event) =>
                onChange(
                  updateConditionRule(rules, index, { value: event.target.value }),
                  `value-${index}`,
                )
              }
              onBlur={onCommitText}
              placeholder={rule.field === "minMessages" ? "1" : "Value"}
              className="h-8 min-w-0 flex-1"
            />
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              className="h-8 w-8 shrink-0"
              onClick={() => onChange(removeConditionRule(rules, index))}
              aria-label="Remove condition"
            >
              <Trash2 className="h-3.5 w-3.5" />
            </Button>
          </div>
        );
      })}
      {rules.length > 1 ? (
        <p className="text-xs text-muted-foreground">All conditions must match.</p>
      ) : null}
      <Button
        type="button"
        variant="ghost"
        size="sm"
        className="h-7 px-2"
        disabled={fields.length === 0}
        onClick={() => onChange(addConditionRule(rules, fields[0]))}
      >
        <Plus className="mr-1 h-3.5 w-3.5" /> Add condition
      </Button>
    </div>
  );
}

function readResponseFields(value: string | null | undefined): string[] {
  if (!value) return [];
  const responseSchema: unknown = JSON.parse(value);
  if (
    typeof responseSchema !== "object" ||
    responseSchema === null ||
    Array.isArray(responseSchema)
  )
    return [];
  const properties = (responseSchema as Record<string, unknown>).properties;
  return typeof properties === "object" && properties !== null && !Array.isArray(properties)
    ? Object.keys(properties)
    : [];
}
