import { readFile } from "node:fs/promises";
import { join } from "node:path";
import {
  isWorkspaceJsonValue,
  MAX_EXTENSION_PARAMS_BYTES,
  MAX_EXTENSION_TAB_KEY_LENGTH,
  MAX_EXTENSION_TAB_TITLE_LENGTH,
} from "@agw/extension-sdk/workspace-params";
import { z } from "zod";

import type {
  JsonValue,
  PersistedFloatingChat,
  PersistedTabGroup,
  WorkspaceTabsFileContent,
} from "../../shared/contracts";
import { tabKey } from "../../shared/workspace-tab-key";
import { writeJsonFile } from "./json-file";

const id = z.string().min(1);
const draftFields = {
  conversationId: id.nullable(),
  draftId: z
    .string()
    .regex(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u),
  draftSubmitted: z.boolean(),
};
const isValidDraft = ({ conversationId, draftSubmitted }: PersistedFloatingChat) =>
  conversationId === null || !draftSubmitted;
const draftError = {
  path: ["draftSubmitted"],
  message: "Only a draft can have a pending submission.",
};

const paramsSchema = z.custom<JsonValue>(isWorkspaceJsonValue).transform((params, ctx) => {
  const serialized = JSON.stringify(params);
  if (Buffer.byteLength(serialized, "utf8") > MAX_EXTENSION_PARAMS_BYTES) {
    ctx.addIssue({ code: "custom", message: "Extension tab params exceed 64 KiB." });
    return z.NEVER;
  }
  return JSON.parse(serialized) as JsonValue;
});

const tabSchema = z.discriminatedUnion("kind", [
  z
    .strictObject({
      id,
      kind: z.literal("chat"),
      preview: z.boolean(),
      ...draftFields,
      title: z.string().nullable(),
    })
    .refine(isValidDraft, draftError),
  z.strictObject({
    id,
    kind: z.literal("file"),
    preview: z.boolean(),
    directoryId: id.nullable(),
    path: z.string().min(1),
    diffScope: z.enum(["staged", "unstaged"]).nullable(),
    diffFileView: z.boolean(),
  }),
  z.strictObject({ id, kind: z.literal("jobs"), preview: z.literal(false) }),
  z.strictObject({
    id,
    kind: z.literal("extension"),
    preview: z.boolean(),
    viewId: id,
    key: z.string().min(1).max(MAX_EXTENSION_TAB_KEY_LENGTH).nullable(),
    title: z.string().min(1).max(MAX_EXTENSION_TAB_TITLE_LENGTH).nullable(),
    params: paramsSchema,
  }),
]);

const groupSchema = z
  .strictObject({
    tabs: z.array(tabSchema),
    activeTabId: id.nullable(),
    recentTabIds: z.array(id),
    floatingChat: z.strictObject(draftFields).refine(isValidDraft, draftError),
  })
  .superRefine(({ tabs, activeTabId, recentTabIds }, ctx) => {
    const ids = new Set(tabs.map((tab) => tab.id));
    if (ids.size !== tabs.length) {
      ctx.addIssue({ code: "custom", path: ["tabs"], message: "Tab IDs must be unique." });
    }
    if (new Set(tabs.map(tabKey)).size !== tabs.length) {
      ctx.addIssue({ code: "custom", path: ["tabs"], message: "Tab identities must be unique." });
    }
    if (tabs.filter((tab) => tab.preview).length > 1) {
      ctx.addIssue({ code: "custom", path: ["tabs"], message: "Only one preview tab is allowed." });
    }
    if (activeTabId !== null && !ids.has(activeTabId)) {
      ctx.addIssue({
        code: "custom",
        path: ["activeTabId"],
        message: "The active tab must exist.",
      });
    }
    const coversAllTabs =
      recentTabIds.length === ids.size &&
      new Set(recentTabIds).size === ids.size &&
      recentTabIds.every((tabId) => ids.has(tabId));
    if (!coversAllTabs) {
      ctx.addIssue({
        code: "custom",
        path: ["recentTabIds"],
        message: "Recent tab IDs must list every tab once.",
      });
    }
  });

const contentSchema = z.strictObject({
  schemaVersion: z.literal(1),
  servers: z.record(id, z.record(id, groupSchema)),
});

type PendingGroup = { serverId: string; projectId: string; group: PersistedTabGroup };
type SaveBatch = { groups: Map<string, PendingGroup>; written: Promise<void> };

export class WorkspaceTabsFile {
  private readonly path: string;
  private queued: Promise<unknown> = Promise.resolve();
  private pending: SaveBatch | null = null;

  public constructor(directory: string) {
    this.path = join(directory, "workspace-tabs.json");
  }

  public read(serverId: string, projectId: string): Promise<PersistedTabGroup | null> {
    return this.enqueue(async () => {
      const { servers } = await this.load();
      const projects = Object.hasOwn(servers, serverId) ? servers[serverId] : undefined;
      return projects && Object.hasOwn(projects, projectId) ? projects[projectId] : null;
    });
  }

  public async save(serverId: string, projectId: string, group: PersistedTabGroup): Promise<void> {
    id.parse(serverId);
    id.parse(projectId);
    const parsed = groupSchema.parse(group);
    if (this.pending === null) {
      const groups = new Map<string, PendingGroup>();
      const written = this.enqueue(async () => {
        if (this.pending?.groups === groups) this.pending = null;
        const content = await this.load();
        for (const entry of groups.values()) {
          const projects = Object.hasOwn(content.servers, entry.serverId)
            ? content.servers[entry.serverId]
            : undefined;
          content.servers = {
            ...content.servers,
            [entry.serverId]: { ...projects, [entry.projectId]: entry.group },
          };
        }
        await writeJsonFile(this.path, content);
      });
      this.pending = { groups, written };
    }
    this.pending.groups.set(JSON.stringify([serverId, projectId]), {
      serverId,
      projectId,
      group: parsed,
    });
    return this.pending.written;
  }

  public pruneProjects(serverId: string, projectIds: readonly string[]): Promise<void> {
    return this.enqueue(async () => {
      const content = await this.load();
      if (!Object.hasOwn(content.servers, serverId)) return;
      const projects = Object.entries(content.servers[serverId]);
      const keep = new Set(projectIds);
      const remaining = projects.filter(([projectId]) => keep.has(projectId));
      if (remaining.length === projects.length) return;
      content.servers[serverId] = Object.fromEntries(remaining);
      await writeJsonFile(this.path, content);
    });
  }

  public pruneServers(profileIds: readonly string[]): Promise<void> {
    return this.enqueue(async () => {
      const content = await this.load();
      const servers = Object.entries(content.servers);
      const keep = new Set(profileIds);
      const remaining = servers.filter(([serverId]) => keep.has(serverId));
      if (remaining.length === servers.length) return;
      await writeJsonFile(this.path, { ...content, servers: Object.fromEntries(remaining) });
    });
  }

  private async load(): Promise<WorkspaceTabsFileContent> {
    try {
      return contentSchema.parse(JSON.parse(await readFile(this.path, "utf8")));
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
      return { schemaVersion: 1, servers: {} };
    }
  }

  private enqueue<T>(operation: () => Promise<T>): Promise<T> {
    // 读取和清理按调用顺序处理，各自结束当前保存批次。
    // Reads and prunes run in call order and seal the current save batch.
    this.pending = null;
    const queued = this.queued.then(operation);
    this.queued = queued.catch(() => undefined);
    return queued;
  }
}
