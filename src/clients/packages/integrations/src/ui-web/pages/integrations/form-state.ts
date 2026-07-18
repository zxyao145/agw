export type FormField = {
  id: string;
  label: string;
  type: "Text" | "Secret" | "Url";
  isRequired: boolean;
  description?: string | null;
};

export type SecretFieldState = {
  configured: boolean;
};

export type SecretFieldFormState = {
  action: "Keep" | "Set" | "Clear";
  secretValue: string;
};

export type SchemaFormState = {
  configuration: Record<string, string>;
  secrets: Record<string, SecretFieldFormState>;
};

export function createSchemaFormState(
  fields: readonly FormField[],
  current?: {
    configuration?: Readonly<Record<string, string | null>>;
    secrets?: Readonly<Record<string, SecretFieldState>>;
  },
): SchemaFormState {
  const configuration: Record<string, string> = {};
  const secrets: Record<string, SecretFieldFormState> = {};

  for (const field of fields) {
    if (field.type === "Secret") {
      const secret = current?.secrets?.[field.id];
      secrets[field.id] = {
        action: secret?.configured || !field.isRequired ? "Keep" : "Set",
        secretValue: "",
      };
      continue;
    }

    configuration[field.id] = current?.configuration?.[field.id] ?? "";
  }

  return { configuration, secrets };
}

export function buildFieldPayload(fields: readonly FormField[], state: SchemaFormState) {
  const configuration: Record<string, string> = {};
  const secrets: Record<
    string,
    {
      action: "Keep" | "Set" | "Clear";
      secretValue: string | null;
    }
  > = {};

  for (const field of fields) {
    if (field.type !== "Secret") {
      configuration[field.id] = state.configuration[field.id]?.trim() ?? "";
      continue;
    }

    const secret = state.secrets[field.id] ?? {
      action: "Keep",
      secretValue: "",
    };
    const isSet = secret.action === "Set";
    secrets[field.id] = {
      action: secret.action,
      secretValue: isSet ? secret.secretValue.trim() || null : null,
    };
  }

  return { configuration, secrets };
}
