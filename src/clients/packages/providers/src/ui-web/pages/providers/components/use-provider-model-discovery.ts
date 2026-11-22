"use client";

import * as React from "react";
import { useMutation } from "@agw/components/query";
import { toast } from "sonner";

import { apiPost, getApiErrorMessage } from "@agw/api";

import { mergeProviderModelDiscoveryResults } from "./provider-models";
import type {
  ProviderModelDiscoveryMutationVariables,
  ProviderModelDiscoveryResponse,
  ProviderModelDiscoveryScope,
} from "./types";

type ScopedProviderModelDiscovery = {
  scope: ProviderModelDiscoveryScope;
  response: ProviderModelDiscoveryResponse;
  discoveredCount: number;
};

export function useProviderModelDiscovery(scope: ProviderModelDiscoveryScope) {
  const currentScopeRef = React.useRef(scope);
  currentScopeRef.current = scope;
  const latestRequestRef = React.useRef<ProviderModelDiscoveryMutationVariables | null>(null);

  const discoveryMutation = useMutation({
    mutationFn: async ({
      scope: requestScope,
      previous,
    }: ProviderModelDiscoveryMutationVariables) => {
      const result = (await apiPost("/api/providers/discover-models", {
        body: {
          providerType: requestScope.providerType,
          endpoint: requestScope.endpoint,
          apiKey: requestScope.apiKey,
        },
      })) as unknown as ProviderModelDiscoveryResponse;

      return {
        scope: requestScope,
        response: mergeProviderModelDiscoveryResults(previous, result),
        discoveredCount: result.modelNames.length,
      } satisfies ScopedProviderModelDiscovery;
    },
    onSuccess: ({ scope: resultScope, discoveredCount }, variables) => {
      if (currentScopeRef.current === resultScope && latestRequestRef.current === variables) {
        toast.success(`Discovered ${discoveredCount} models`);
      }
    },
    onError: (error, variables) => {
      if (currentScopeRef.current === variables.scope && latestRequestRef.current === variables) {
        toast.error(`Model discovery failed: ${getApiErrorMessage(error)}`);
      }
    },
  });

  const latestVariables = discoveryMutation.variables;
  const response =
    discoveryMutation.data?.scope === scope
      ? discoveryMutation.data.response
      : latestVariables?.scope === scope
        ? latestVariables.previous
        : undefined;
  const isCurrentMutation = latestVariables?.scope === scope;

  const fetchModels = () => {
    if (!scope.open) {
      return;
    }

    const variables = { scope, previous: response };
    latestRequestRef.current = variables;
    discoveryMutation.mutate(variables);
  };

  return {
    data: response,
    isPending: isCurrentMutation && discoveryMutation.isPending,
    fetchModels,
  };
}
