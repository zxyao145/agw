import type { ExtensionApi } from "@agw/extension-sdk";
import { z } from "zod";

const proxyUrlSchema = z
  .string()
  .trim()
  .regex(/^\P{Cc}*$/u)
  .pipe(
    z.union([
      z.literal(""),
      z.url({ protocol: /^(?:https?|socks4a?|socks5h?)$/u }).pipe(
        z.string().refine((value) => {
          const url = new URL(value);
          return url.hostname !== "" && url.search === "" && url.hash === "";
        }, "Use a proxy URL with a host and optional port."),
      ),
    ]),
  );
const settingsSchema = z.strictObject({ proxyUrl: proxyUrlSchema });

function validateSettings(value: unknown) {
  const result = settingsSchema.safeParse(value);
  if (!result.success) throw new Error("Enter a valid HTTP, HTTPS or SOCKS proxy URL.");
  return result.data;
}

function settingsUri(api: ExtensionApi): string {
  const { extension, server, project } = api.context.get();
  if (!project) throw new Error("Select a Project to use its repository proxy settings.");
  return `${extension.storageUri.replace(/\/+$/u, "")}/proxy-${encodeURIComponent(server.id)}/project-${encodeURIComponent(project.id)}.json`;
}

export async function readProxyUrl(
  api: ExtensionApi,
  signal?: AbortSignal,
): Promise<string | undefined> {
  const { server, project } = api.context.get();
  let text: string | undefined;
  try {
    text = await api.fs.readTextFile(settingsUri(api), { signal });
  } catch (error) {
    if ((error as { code?: string }).code !== "ENOENT") throw error;
  }
  const current = api.context.get();
  if (current.server.id !== server.id || current.project?.id !== project?.id)
    throw new Error("The active Server or Project changed. Retry the operation.");
  return text === undefined ? undefined : validateSettings(JSON.parse(text)).proxyUrl || undefined;
}

export async function writeProxyUrl(api: ExtensionApi, proxyUrl: string): Promise<void> {
  const settings = validateSettings({ proxyUrl });
  await api.fs.writeFile(settingsUri(api), JSON.stringify(settings, null, 2));
}
