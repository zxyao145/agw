import { apiGet, apiPost, type components } from "@agw/api";

export type ServerExtensionStatus = components["schemas"]["ExtensionManagementStatus"];

export async function listServerExtensions(): Promise<ServerExtensionStatus[]> {
  const extensions = await apiGet("/api/extensions");
  if (extensions == null) throw new Error("The server returned no extension list.");
  return extensions;
}

export async function setServerExtensionEnabled(
  extensionId: string,
  enabled: boolean,
): Promise<ServerExtensionStatus> {
  const status = enabled
    ? await apiPost("/api/extensions/enable", { body: { extensionId } })
    : await apiPost("/api/extensions/disable", { body: { extensionId } });
  if (status == null) throw new Error("The server returned no extension status.");
  return status;
}
