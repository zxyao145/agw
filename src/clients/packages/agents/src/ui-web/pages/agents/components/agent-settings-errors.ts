import {
  getAgentEnvironmentVariablesError,
  type AgentEnvironmentVariableEntry,
} from "./agent-environment-variables";
import { getAgentExtraSettingsError } from "./agent-extra-settings";
import { getAgentResponseSchemaError } from "./agent-response-schema";

export function getAgentSettingsErrors({
  isExternalAgent,
  isA2A,
  extra,
  responseSchema,
  environmentVariables,
}: {
  isExternalAgent: boolean;
  isA2A: boolean;
  extra: string;
  responseSchema: string;
  environmentVariables: AgentEnvironmentVariableEntry[];
}) {
  return {
    extraError: isExternalAgent && !isA2A ? getAgentExtraSettingsError(extra) : null,
    responseSchemaError: isA2A ? null : getAgentResponseSchemaError(responseSchema),
    environmentVariablesError: isA2A
      ? null
      : getAgentEnvironmentVariablesError(environmentVariables),
  };
}
