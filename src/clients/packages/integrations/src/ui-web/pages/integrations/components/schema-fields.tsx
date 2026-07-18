import { Input } from "@agw/components";
import { Label } from "@agw/components";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@agw/components";

import type { FormField, SchemaFormState, SecretFieldFormState } from "../form-state";

type SchemaFieldsProps = {
  fields: readonly FormField[];
  form: SchemaFormState;
  idPrefix: string;
  onChange: (form: SchemaFormState) => void;
};

export function SchemaFields({ fields, form, idPrefix, onChange }: SchemaFieldsProps) {
  if (fields.length === 0) {
    return (
      <p className="rounded-lg border border-dashed p-3 text-sm text-muted-foreground">
        This authentication scheme does not require additional fields at this scope.
      </p>
    );
  }

  const updateSecret = (fieldId: string, update: Partial<SecretFieldFormState>) => {
    onChange({
      ...form,
      secrets: {
        ...form.secrets,
        [fieldId]: { ...form.secrets[fieldId], ...update },
      },
    });
  };

  return (
    <div className="grid gap-5">
      {fields.map((field) => {
        const inputId = `${idPrefix}-${field.id}`;
        if (field.type !== "Secret") {
          return (
            <div key={field.id} className="grid gap-2">
              <Label htmlFor={inputId}>
                {field.label}
                {field.isRequired ? " *" : ""}
              </Label>
              <Input
                id={inputId}
                type={field.type === "Url" ? "url" : "text"}
                value={form.configuration[field.id] ?? ""}
                onChange={(event) =>
                  onChange({
                    ...form,
                    configuration: {
                      ...form.configuration,
                      [field.id]: event.target.value,
                    },
                  })
                }
                required={field.isRequired}
                autoComplete="off"
              />
              {field.description ? (
                <p className="text-xs text-muted-foreground">{field.description}</p>
              ) : null}
            </div>
          );
        }

        const secret = form.secrets[field.id];
        return (
          <div key={field.id} className="grid gap-3 rounded-lg border border-dashed p-4">
            <div>
              <Label>
                {field.label}
                {field.isRequired ? " *" : ""}
              </Label>
              {field.description ? (
                <p className="mt-1 text-xs text-muted-foreground">{field.description}</p>
              ) : null}
            </div>
            <div className="grid gap-2">
              <div className="grid gap-2">
                <Label htmlFor={`${inputId}-action`}>Update</Label>
                <Select
                  value={secret.action}
                  onValueChange={(action: SecretFieldFormState["action"]) =>
                    updateSecret(field.id, { action })
                  }
                >
                  <SelectTrigger id={`${inputId}-action`} className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="Keep">Keep current value</SelectItem>
                    <SelectItem value="Set">Set new value</SelectItem>
                    <SelectItem value="Clear">Clear value</SelectItem>
                  </SelectContent>
                </Select>
              </div>
            </div>
            {secret.action === "Set" ? (
              <Input
                id={inputId}
                type="password"
                value={secret.secretValue}
                onChange={(event) => updateSecret(field.id, { secretValue: event.target.value })}
                placeholder="Stored encrypted; never returned by the API"
                autoComplete="new-password"
              />
            ) : null}
          </div>
        );
      })}
    </div>
  );
}
