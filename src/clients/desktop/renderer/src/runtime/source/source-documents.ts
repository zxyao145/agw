import type { FileViewerFile } from "@agw/extension-sdk";
import { SourceDocument, type SourceDocumentServices } from "./source-document";

type Entry = {
  document: SourceDocument | null;
  opening: Promise<SourceDocument>;
};

export class SourceDocuments {
  private readonly entries = new Map<string, Entry>();

  public constructor(private readonly onOpen?: (tabId: string) => void) {}

  public get(tabId: string): SourceDocument | null {
    return this.entries.get(tabId)?.document ?? null;
  }

  public open(
    tabId: string,
    file: FileViewerFile,
    services: SourceDocumentServices,
  ): Promise<SourceDocument> {
    const current = this.entries.get(tabId);
    if (current) return current.opening;
    this.onOpen?.(tabId);
    const opening = SourceDocument.open(file, {
      ...services,
      setUnsaved: (name) => {
        if (this.entries.get(tabId) !== entry) return;
        services.setUnsaved(name);
      },
    }).then(
      (document) => {
        if (this.entries.get(tabId) === entry) entry.document = document;
        else document.dispose();
        return document;
      },
      (error: unknown) => {
        if (this.entries.get(tabId) === entry) this.entries.delete(tabId);
        throw error;
      },
    );
    const entry: Entry = { document: null, opening };
    this.entries.set(tabId, entry);
    return opening;
  }

  public release(tabId: string, keepUnsaved: boolean): void {
    const entry = this.entries.get(tabId);
    if (!entry) return;
    if (keepUnsaved && this.hasUnsaved(tabId)) return;
    entry.document?.dispose();
    this.entries.delete(tabId);
  }

  public hasUnsaved(tabId: string): boolean {
    return this.get(tabId)?.state.unsaved ?? false;
  }

  public dispose(): void {
    for (const id of this.entries.keys()) this.release(id, false);
  }
}
