import type { SearchableSelectOption } from "@/components/SearchableSelect/searchable-select";

import type { ConnectionOption } from "./types";

export interface SelectedOptionItem {
  id: string;
  title: string;
  description?: string;
}

export function buildConnectionOptionLabel(
  connection: Pick<ConnectionOption, "displayName" | "alias">,
): string {
  return `${connection.displayName} · ${connection.alias}`;
}

export function buildConnectionSelectOptions(
  connections: readonly ConnectionOption[],
  selectedConnectionIds: readonly string[],
): SearchableSelectOption[] {
  return connections
    .filter(
      (connection) =>
        connection.status === "Ready" || selectedConnectionIds.includes(connection.id),
    )
    .map((connection) => ({
      value: connection.id,
      title: buildConnectionOptionLabel(connection),
      subtitle: [connection.connectorId, connection.status, connection.subject]
        .filter(Boolean)
        .join(" · "),
      group: connection.status === "Ready" ? "Ready Connections" : "Existing Bindings",
    }));
}

export function buildSelectedConnectionItems(
  selectedConnectionIds: readonly string[],
  connections: readonly ConnectionOption[],
): SelectedOptionItem[] {
  return selectedConnectionIds.map((connectionId) => {
    const connection = connections.find((candidate) => candidate.id === connectionId);
    return connection
      ? {
          id: connectionId,
          title: buildConnectionOptionLabel(connection),
          description: `${connection.connectorId} · ${connection.status}`,
        }
      : {
          id: connectionId,
          title: connectionId,
          description: "Connection unavailable",
        };
  });
}
