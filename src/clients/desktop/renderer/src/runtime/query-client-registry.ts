import type { QueryClient } from "@agw/components/query";
import type { ServerProfile } from "@desktop/shared/contracts";

type QueryClientEntry = {
  baseUrl: string;
  token: string | null;
  client: QueryClient;
};

function normalizeBaseUrl(baseUrl: string): string {
  return baseUrl.replace(/\/+$/u, "");
}

/** 每个 profile 最多保留一份缓存；授权身份或地址变化时立即冷启动。 */
export class ServerQueryClientRegistry {
  private readonly entries = new Map<string, QueryClientEntry>();

  public constructor(private readonly createClient: () => QueryClient) {}

  public get(profile: ServerProfile, token: string | null): QueryClient {
    const baseUrl = normalizeBaseUrl(profile.baseUrl);
    const existing = this.entries.get(profile.id);
    if (existing?.baseUrl === baseUrl && existing.token === token) return existing.client;

    if (existing) this.retire(existing.client);
    const client = this.createClient();
    this.entries.set(profile.id, { baseUrl, token, client });
    return client;
  }

  /** 清理已从 Desktop settings 删除的 profile 缓存。 */
  public prune(profileIds: Iterable<string>): void {
    const retainedIds = new Set(profileIds);
    for (const [profileId, entry] of this.entries) {
      if (retainedIds.has(profileId)) continue;
      this.retire(entry.client);
      this.entries.delete(profileId);
    }
  }

  public dispose(): void {
    for (const entry of this.entries.values()) this.retire(entry.client);
    this.entries.clear();
  }

  private retire(client: QueryClient): void {
    void client.cancelQueries();
    client.clear();
  }
}
