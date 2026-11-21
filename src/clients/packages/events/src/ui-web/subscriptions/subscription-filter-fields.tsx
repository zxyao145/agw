import { Fragment, useRef } from "react";
import type { JsonValue } from "@agw/api";
import { Badge, Button, cn, Input, Label } from "@agw/components";
import { ChevronDownIcon, ChevronUpIcon, XIcon } from "lucide-react";

import { readCommaSeparatedValues } from "../comma-separated-values";
import type { SubscriptionFieldsProps } from "./subscription-fields";
import type { FilterField, FilterRow } from "./subscription-filter";

export function SubscriptionFilterFields({
  form,
  readOnly,
  update,
  existingFilter,
  errors,
}: SubscriptionFieldsProps & {
  existingFilter?: JsonValue;
  errors: string[];
}) {
  const showJson = readOnly || form.unsupportedFilter !== null;
  const canClear = !readOnly && form.unsupportedFilter !== null;
  return (
    <details className="rounded-md border p-3 md:col-span-2">
      <summary className="cursor-pointer text-sm font-medium">Advanced settings</summary>
      <div className="mt-4 space-y-3">
        <div className="flex items-center justify-between">
          <Label>Filter conditions</Label>
          {canClear ? (
            <Button
              variant="outline"
              size="sm"
              onClick={() => update({ unsupportedFilter: null, filterRows: [] })}
            >
              Clear conditions
            </Button>
          ) : null}
        </div>
        {showJson ? (
          <pre className="max-h-48 overflow-auto rounded-md border p-2 font-mono text-xs">
            {JSON.stringify(form.unsupportedFilter ?? existingFilter ?? null, null, 2)}
          </pre>
        ) : (
          <FilterRows form={form} update={update} errors={errors} />
        )}
      </div>
    </details>
  );
}

function FilterRows({
  form,
  update,
  errors,
}: Pick<SubscriptionFieldsProps, "form" | "update"> & { errors: string[] }) {
  const changeRow = (index: number, changes: Partial<FilterRow>) =>
    update({
      filterRows: form.filterRows.map((row, rowIndex) =>
        rowIndex === index ? { ...row, ...changes } : row,
      ),
    });
  const removeRow = (index: number) =>
    update({ filterRows: form.filterRows.filter((_, rowIndex) => rowIndex !== index) });
  const addRow = () =>
    update({ filterRows: [...form.filterRows, { field: "scope", dataPath: "", values: [] }] });
  return (
    <>
      <div className="grid gap-1 empty:hidden">
        {form.filterRows.map((row, index) => (
          <Fragment key={index}>
            {index > 0 ? (
              <div className="flex justify-center">
                <FilterOperator operator="AND" />
              </div>
            ) : null}
            <FilterCondition
              row={row}
              number={index + 1}
              change={(changes) => changeRow(index, changes)}
              remove={() => removeRow(index)}
            />
          </Fragment>
        ))}
      </div>
      {errors.length > 0 ? (
        <p role="alert" className="text-xs text-destructive">
          {errors[0]}
        </p>
      ) : null}
      <Button variant="outline" size="sm" onClick={addRow}>
        Add condition
      </Button>
    </>
  );
}

function FilterOperator({ operator }: { operator: "AND" | "OR" }) {
  const isAnd = operator === "AND";
  const Icon = isAnd ? ChevronUpIcon : ChevronDownIcon;
  return (
    <Badge
      variant="secondary"
      role="img"
      aria-label={operator}
      title={isAnd ? "Match every condition" : "Match any value in this condition"}
    >
      <Icon aria-hidden="true" />
      {operator}
    </Badge>
  );
}

type FilterConditionProps = {
  row: FilterRow;
  number: number;
  change(changes: Partial<FilterRow>): void;
  remove(): void;
};

function FilterCondition({ row, number, change, remove }: FilterConditionProps) {
  return (
    <div className="grid items-start gap-2 rounded-md border p-2 sm:grid-cols-[8rem_1fr_1fr_auto]">
      <select
        aria-label={`Condition ${number} field`}
        className="h-9 rounded-md border bg-background px-2 text-sm"
        value={row.field}
        onChange={(event) =>
          change({ field: event.currentTarget.value as FilterField, dataPath: "" })
        }
      >
        <option value="subject">Subject</option>
        <option value="scope">Scope</option>
        <option value="tags">Tags</option>
        <option value="data">Data path</option>
      </select>
      {row.field === "data" ? <FilterDataPath {...{ row, number, change }} /> : null}
      <FilterValues {...{ row, number, change }} />
      <Button
        variant="outline"
        size="sm"
        aria-label={`Remove condition ${number}`}
        onClick={remove}
      >
        Remove
      </Button>
    </div>
  );
}

function FilterValues({
  row,
  number,
  change,
}: Pick<FilterConditionProps, "row" | "number" | "change">) {
  const inputRef = useRef<HTMLInputElement>(null);
  const addValues = (values: string[]) => {
    if (values.length > 0) change({ values: [...row.values, ...values], draftValue: "" });
  };
  const commitDraft = () => addValues(readCommaSeparatedValues(row.draftValue ?? ""));

  return (
    <div className={cn("min-w-0 space-y-1.5", row.field !== "data" && "sm:col-span-2")}>
      <div className="flex flex-wrap items-center gap-1.5 empty:hidden">
        {row.values.map((value, index) => (
          <div key={index} className="flex max-w-full items-center gap-1.5">
            {index > 0 ? <FilterOperator operator="OR" /> : null}
            <Badge variant="outline" className="min-w-0 shrink pr-0.5">
              <span className="truncate" title={value}>
                {value}
              </span>
              <Button
                type="button"
                variant="ghost"
                size="icon-sm"
                className="size-6 rounded-full focus-visible:ring-inset [&_svg]:size-3"
                aria-label={`Remove value ${value} from condition ${number}`}
                onClick={() => {
                  change({ values: row.values.filter((_, valueIndex) => valueIndex !== index) });
                  inputRef.current?.focus();
                }}
              >
                <XIcon aria-hidden="true" />
              </Button>
            </Badge>
          </div>
        ))}
      </div>
      <Input
        ref={inputRef}
        aria-label={`Condition ${number} values`}
        value={row.draftValue ?? ""}
        placeholder="Type a value and press Enter"
        title="Paste a comma-separated list to add multiple values"
        enterKeyHint="done"
        onChange={(event) => change({ draftValue: event.currentTarget.value })}
        onBlur={commitDraft}
        onKeyDown={(event) => {
          if (event.key !== "Enter" || event.nativeEvent.isComposing) return;
          event.preventDefault();
          commitDraft();
        }}
        onPaste={(event) => {
          const pasted = event.clipboardData.getData("text/plain");
          if (!pasted.includes(",")) return;
          const { value, selectionStart, selectionEnd } = event.currentTarget;
          const start = selectionStart ?? value.length;
          const end = selectionEnd ?? start;
          const pastedValue = `${value.slice(0, start)}${pasted}${value.slice(end)}`;
          event.preventDefault();
          addValues(readCommaSeparatedValues(pastedValue));
        }}
      />
    </div>
  );
}

function FilterDataPath({
  row,
  number,
  change,
}: Pick<FilterConditionProps, "row" | "number" | "change">) {
  return (
    <div className="flex items-center rounded-md border">
      <span className="px-2 font-mono text-xs">data.</span>
      <Input
        aria-label={`Condition ${number} data path`}
        className="border-0"
        value={row.dataPath}
        onChange={(event) => change({ dataPath: event.currentTarget.value })}
      />
    </div>
  );
}
