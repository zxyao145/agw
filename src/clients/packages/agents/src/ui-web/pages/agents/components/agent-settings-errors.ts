import {
  getAgentEnvironmentVariablesError,
  type AgentEnvironmentVariableEntry,
} from "./agent-environment-variables";
import { getAgentExtraSettingsError } from "./agent-extra-settings";
import { getAgentResponseSchemaError } from "./agent-response-schema";
import { getAcpSettingsError } from "./acp-settings";

export function getAgentSettingsErrors({
  isExternalAgent,
  isA2A,
  isAcp,
  extra,
  responseSchema,
  environmentVariables,
}: {
  isExternalAgent: boolean;
  isA2A: boolean;
  isAcp: boolean;
  extra: string;
  responseSchema: string;
  environmentVariables: AgentEnvironmentVariableEntry[];
}) {
  return {
    extraError: isAcp
      ? getAcpSettingsError(extra)
      : isExternalAgent && !isA2A
        ? getAgentExtraSettingsError(extra)
        : null,
    responseSchemaError: isA2A || isAcp ? null : getAgentResponseSchemaError(responseSchema),
    environmentVariablesError: isA2A
      ? null
      : getAgentEnvironmentVariablesError(environmentVariables),
  };
}
