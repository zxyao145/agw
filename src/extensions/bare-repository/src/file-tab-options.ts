import type { OpenMainViewOptions } from "@agw/extension-sdk";
import { MAX_EXTENSION_TAB_TITLE_LENGTH } from "@agw/extension-sdk/workspace-params";
import type { FileParams } from "./view";

const titleSegments = new Intl.Segmenter(undefined, { granularity: "grapheme" });

function fileTabTitle(path: string): string {
  const name = path.slice(path.lastIndexOf("/") + 1);
  if (name.length <= MAX_EXTENSION_TAB_TITLE_LENGTH) return name;

  let prefix = "";
  for (const { segment } of titleSegments.segment(name)) {
    if (prefix.length + segment.length >= MAX_EXTENSION_TAB_TITLE_LENGTH) break;
    prefix += segment;
  }
  return `${prefix}…`;
}

export async function repositoryFileTabOptions(
  { repository, branch, path }: FileParams,
  preview: boolean,
): Promise<Required<OpenMainViewOptions>> {
  const identity = new TextEncoder().encode(JSON.stringify([repository, branch, path]));
  const digest = await crypto.subtle.digest("SHA-256", identity);
  const key = Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0")).join(
    "",
  );
  return { key, title: fileTabTitle(path), preview };
}
