import { apiGet, apiPut } from "./client";

export type QuickPromptKind = "system" | "user";
export type QuickPrompt = {
  id: string;
  label: string;
  text: string;
  description?: string | null;
  kind: QuickPromptKind;
};
export type QuickPromptDraft = Omit<QuickPrompt, "kind">;
export type QuickPromptManage = {
  system: { items: QuickPrompt[] };
  systemVersion?: number | null;
  user: { items: QuickPrompt[] };
  userVersion?: number | null;
  canManageSystem: boolean;
};

const get = (path: string, options?: unknown) =>
  apiGet(path as never, options as never) as Promise<unknown>;
const put = (path: string, options?: unknown) =>
  apiPut(path as never, options as never) as Promise<unknown>;

export async function listQuickPrompts(): Promise<QuickPrompt[]> {
  return (await get("/api/quick-prompts")) as QuickPrompt[];
}

export async function getQuickPromptManagement(): Promise<QuickPromptManage> {
  return (await get("/api/quick-prompts/manage")) as QuickPromptManage;
}

export async function saveQuickPrompts(
  kind: QuickPromptKind,
  version: number | null,
  items: QuickPromptDraft[],
): Promise<void> {
  await put(`/api/quick-prompts?kind=${kind}`, { body: { version, items } });
}
