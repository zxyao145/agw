export type ResourceOwner = {
  extensionId: string;
  frameId: string;
  pageId: string;
};

/** 按 `(extensionId, frameId, pageId)` 记录的长期资源。Long-lived resources recorded per `(extensionId, frameId, pageId)`. */
export class ExtensionResourceTracker<T> {
  private readonly entries = new Map<string, { owner: ResourceOwner; resource: T }>();

  public constructor(private readonly release: (resource: T) => void) {}

  public add(key: string, owner: ResourceOwner, resource: T): void {
    if (this.entries.has(key)) throw new Error(`Resource ${key} is already registered.`);
    this.entries.set(key, { owner, resource });
  }

  public get(key: string): { owner: ResourceOwner; resource: T } | undefined {
    return this.entries.get(key);
  }

  /** 移除但不释放（资源已经自行结束）。Removes without releasing (the resource already ended). */
  public forget(key: string): void {
    this.entries.delete(key);
  }

  public remove(key: string): boolean {
    const entry = this.entries.get(key);
    if (!entry) return false;
    this.entries.delete(key);
    this.release(entry.resource);
    return true;
  }

  public count(predicate: (owner: ResourceOwner) => boolean): number {
    return [...this.entries.values()].filter((entry) => predicate(entry.owner)).length;
  }

  public releaseWhere(predicate: (owner: ResourceOwner) => boolean): void {
    for (const [key, entry] of this.entries) {
      if (!predicate(entry.owner)) continue;
      this.entries.delete(key);
      this.release(entry.resource);
    }
  }

  public releasePage(frameId: string, pageId: string): void {
    this.releaseWhere((owner) => owner.frameId === frameId && owner.pageId === pageId);
  }

  public releaseFrame(extensionId: string, frameId: string): void {
    this.releaseWhere((owner) => owner.extensionId === extensionId && owner.frameId === frameId);
  }

  public releaseExtension(extensionId: string): void {
    this.releaseWhere((owner) => owner.extensionId === extensionId);
  }

  public releaseAll(): void {
    this.releaseWhere(() => true);
  }
}
