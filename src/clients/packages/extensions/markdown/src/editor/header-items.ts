import type {
  Disposable,
  DocumentSession,
  ExtensionApi,
  FileHeaderItem,
  FileHeaderMenuEntry,
} from "@agw/extension-sdk";

import type { TextEncodingOption } from "../shared/document-error";
import { BOM_ENCODINGS, encodingName } from "../shared/editor-chrome";
import { errorMessage, notifyFailure } from "../shared/errors";

const ENCODING_ITEM_ID = "encoding";
const SAVE_ITEM_ID = "save";

/**
 * Mixed 在宿主文件标题栏 More 菜单中的条目（文件标题栏条目设计第 3.1 节）：
 * - 编码：显示编码名称的子菜单，其中 "Reopen with Encoding" 与 "Save with Encoding" 列出 `fs.listTextEncodings` 的编码，
 *   保存时可以带 BOM 的编码另有 "with BOM" 一项；编码列表读取失败时显示通知，没有这一项。
 * - Save：没有未保存修改时不可用。
 * 编码列表读取结束后，会话有编码并且没有失败时声明条目，否则声明空数组；结果不变时不再发送。条目只随会话的变化更新，
 * 输入不经过 React 提交。
 * Mixed's items in the More menu of the host's file header (file header items design, section 3.1):
 * - The encoding: a submenu named after the encoding, whose "Reopen with Encoding" and "Save with Encoding" list the
 *   `fs.listTextEncodings` encodings, with a "with BOM" entry for encodings that can have one when saving; when the
 *   list cannot be read, a notification shows and this item is left out.
 * - Save: unavailable without unsaved changes.
 * Once the encoding list is read, the items are declared while the session has an encoding and has not failed,
 * otherwise an empty array; unchanged results are not sent again. The items follow session changes only, so typing
 * goes through no React commit.
 */
export function publishHeaderItems(api: ExtensionApi, session: DocumentSession): Disposable {
  let disposed = false;
  /** 编码列表；读取中为 null，读取失败为空数组。The encodings; null while reading, empty after a failed read. */
  let encodings: readonly TextEncodingOption[] | null = null;
  /** 最近一次发送的条目（JSON）。The items sent last, as JSON. */
  let sent = "[]";
  /** 最近一次计算的编码菜单项对应的操作。The operations of the encoding entries computed last. */
  let actions = new Map<string, () => Promise<void>>();

  const encodingItem = (
    label: string,
    list: readonly TextEncodingOption[],
    next: Map<string, () => Promise<void>>,
  ): FileHeaderItem => {
    const entry = (id: string, entryLabel: string, action: () => Promise<void>) => {
      next.set(id, action);
      return { id, label: entryLabel } satisfies FileHeaderMenuEntry;
    };
    return {
      id: ENCODING_ITEM_ID,
      placement: "top",
      label,
      menu: [
        {
          id: "reopen",
          label: "Reopen with Encoding",
          menu: list.map((encoding) =>
            entry(`reopen:${encoding.id}`, encoding.label, () =>
              session.reopenWithEncoding(encoding.id),
            ),
          ),
        },
        {
          id: "save",
          label: "Save with Encoding",
          menu: list.flatMap((encoding) =>
            [false, ...(BOM_ENCODINGS.has(encoding.id) ? [true] : [])].map((bom) =>
              entry(
                bom ? `save:${encoding.id}:bom` : `save:${encoding.id}`,
                bom ? `${encoding.label} with BOM` : encoding.label,
                () => session.saveWithEncoding(encoding.id, bom),
              ),
            ),
          ),
        },
      ],
    };
  };

  const update = () => {
    if (disposed || encodings === null) return;
    const { format, state } = session;
    const next = new Map<string, () => Promise<void>>();
    const items: FileHeaderItem[] =
      format === null || state === "failed"
        ? []
        : [
            ...(encodings.length > 0 ? [encodingItem(encodingName(format), encodings, next)] : []),
            { id: SAVE_ITEM_ID, placement: "top", label: "Save", disabled: state !== "dirty" },
          ];
    actions = next;
    const json = JSON.stringify(items);
    if (json === sent) return;
    sent = json;
    void api.workspace.setFileHeaderItems(items).catch((error: unknown) => console.error(error));
  };

  const changes = session.onDidChange(update);
  // 编码菜单项不存在时条目已经更新，选择不再有效。A missing encoding entry means the item changed and the choice is stale.
  const selections = api.workspace.onDidSelectFileHeaderItem(({ itemId, entryId }) => {
    if (itemId === SAVE_ITEM_ID && entryId === null) {
      notifyFailure(api, session.save());
      return;
    }
    if (itemId !== ENCODING_ITEM_ID || entryId === null) return;
    const action = actions.get(entryId);
    if (action) notifyFailure(api, action());
  });
  api.fs.listTextEncodings().then(
    (list) => {
      encodings = list;
      update();
    },
    (error: unknown) => {
      if (disposed) return;
      api.ui.notify({ type: "error", message: errorMessage(error) });
      encodings = [];
      update();
    },
  );

  return {
    dispose() {
      disposed = true;
      changes.dispose();
      selections.dispose();
      if (sent === "[]") return;
      sent = "[]";
      void api.workspace.setFileHeaderItems([]).catch((error: unknown) => console.error(error));
    },
  };
}
