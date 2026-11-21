import type { ComponentProps, ReactNode } from "react";
import { Button, DialogFooter, Input, Label, Textarea } from "@agw/components";

const NATIVE_SELECT_CLASS_NAME = "h-9 w-full rounded-md border bg-background px-3 text-sm";

export function EventEditorFooter({
  readOnly,
  pending,
  canSave,
  saveLabel,
  onClose,
  onSave,
}: {
  readOnly: boolean;
  pending: boolean;
  canSave: boolean;
  saveLabel: string;
  onClose(): void;
  onSave(): void;
}) {
  return (
    <DialogFooter>
      <Button variant="outline" onClick={onClose} disabled={pending}>
        {readOnly ? "Close" : "Cancel"}
      </Button>
      {readOnly ? null : (
        <Button onClick={onSave} disabled={!canSave}>
          {pending ? "Saving…" : saveLabel}
        </Button>
      )}
    </DialogFooter>
  );
}

type LabeledControl = { id: string; label: string };
type LabeledInputProps = Omit<ComponentProps<typeof Input>, "id" | "onChange"> &
  LabeledControl & { description?: ReactNode; onValueChange(value: string): void };
type LabeledSelectProps = Omit<ComponentProps<"select">, "id" | "onChange"> &
  LabeledControl & { onValueChange(value: string): void };
type LabeledTextareaProps = Omit<ComponentProps<typeof Textarea>, "id" | "onChange"> &
  LabeledControl & { monospace?: boolean; onValueChange(value: string): void };

export function LabeledInput({
  id,
  label,
  description,
  onValueChange,
  ...props
}: LabeledInputProps) {
  return (
    <div className="space-y-2">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} {...props} onChange={(event) => onValueChange(event.currentTarget.value)} />
      {description}
    </div>
  );
}

export function LabeledSelect({ id, label, onValueChange, ...props }: LabeledSelectProps) {
  return (
    <div className="space-y-2">
      <Label htmlFor={id}>{label}</Label>
      <select
        id={id}
        className={NATIVE_SELECT_CLASS_NAME}
        {...props}
        onChange={(event) => onValueChange(event.currentTarget.value)}
      />
    </div>
  );
}

export function LabeledTextarea({
  id,
  label,
  monospace,
  onValueChange,
  ...props
}: LabeledTextareaProps) {
  return (
    <div className="space-y-2">
      <Label htmlFor={id}>{label}</Label>
      <Textarea
        id={id}
        className={monospace ? "font-mono text-xs" : undefined}
        {...props}
        onChange={(event) => onValueChange(event.currentTarget.value)}
      />
    </div>
  );
}
