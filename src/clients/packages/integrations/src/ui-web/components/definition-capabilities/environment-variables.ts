export interface EnvironmentVariableEntry {
  key: string;
  value: string;
}

export function getEnvironmentVariablesError(entries: EnvironmentVariableEntry[]): string | null {
  const names = new Set<string>();

  for (const entry of entries) {
    const key = entry.key.trim();
    if (!key) {
      return "Environment variable key is required.";
    }
    if (key.includes("=") || key.includes("\0")) {
      return "Environment variable key cannot contain '=' or a null character.";
    }
    if (names.has(key)) {
      return "Environment variable keys must be unique.";
    }

    names.add(key);
  }

  return null;
}

export function normalizeEnvironmentVariables(
  entries: EnvironmentVariableEntry[],
): Record<string, string> {
  const error = getEnvironmentVariablesError(entries);
  if (error) {
    throw new Error(error);
  }

  return Object.fromEntries(entries.map((entry) => [entry.key.trim(), entry.value]));
}

export function toEnvironmentVariableEntries(
  environmentVariables: Record<string, string> | null | undefined,
): EnvironmentVariableEntry[] {
  return Object.entries(environmentVariables ?? {}).map(([key, value]) => ({ key, value }));
}
