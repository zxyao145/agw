import type { ExtensionApi } from "@agw/extension-sdk";
import { EditorContent } from "@tiptap/react";
import { useState, useSyncExternalStore } from "react";
import type { Root } from "react-dom/client";

import { DocumentError } from "../shared/document-error";
import { useSessionValue } from "../shared/editor-chrome";
import { notifyFailure } from "../shared/errors";
import { createFindBarMemory, FindBar } from "../shared/find-bar";
import type { Platform } from "../shared/find-query";
import { OutlinePanel, useActiveHeading } from "../shared/outline-panel";
import { BlockHandle } from "./block-handle";
import type { EditDocument } from "./edit-document";
import { FormattingMenu } from "./formatting-menu";
import { ImageInsert, ImageMenu } from "./image-menu";
import { LinkMenu } from "./link-menu";
import { SlashMenu } from "./slash-menu";
import { TableToolbar } from "./table-toolbar";

/**
 * Mixed 查看器（设计 5.7）：内容列、标题大纲与编辑器菜单。保存状态由宿主显示在文件名后，编码与 Save 在宿主文件标题栏的
 * More 菜单中（header-items.ts）。文档会话读取中显示 "Loading"，为 Failed 时显示说明面板。
 * The Mixed viewer (design section 5.7): the content column, heading outline, and editor menus.
 * The host shows the save status after the file name, and the encoding and Save in the More menu of its file header
 * (header-items.ts). A loading session shows "Loading" and a Failed one the explanation panel.
 */
export function EditView({ api, editDocument }: { api: ExtensionApi; editDocument: EditDocument }) {
  const { editor, session, ui } = editDocument;
  // 只在读取中、失败与可以编辑之间变化时重新渲染，未保存状态不触发渲染。Renders again only between loading, failed and
  // editable; the unsaved state triggers no render.
  const phase = useSessionValue(session, ({ state }) =>
    state === "loading" || state === "failed" ? state : "ready",
  );
  const fileUri = () => session.file.uri;

  return (
    <div className="edit-viewer">
      {phase === "loading" ? <p className="status">Loading {session.file.name}…</p> : null}
      {phase === "failed" ? (
        <DocumentError
          fileName={session.file.name}
          error={session.error}
          listEncodings={() => api.fs.listTextEncodings()}
          onReopenWithEncoding={(encoding) =>
            notifyFailure(api, session.reopenWithEncoding(encoding))
          }
        />
      ) : null}
      <EditDocumentLayout
        editDocument={editDocument}
        ready={phase === "ready"}
        platform={api.context.get().app.platform}
      />
      <BlockHandle editor={editor} />
      <FormattingMenu editor={editor} ui={ui} />
      <LinkMenu api={api} editor={editor} ui={ui} fileUri={fileUri} />
      <ImageMenu editor={editor} />
      <ImageInsert editor={editor} ui={ui} />
      <TableToolbar editor={editor} />
      <SlashMenu store={editDocument.slashMenu} />
    </div>
  );
}

function EditDocumentLayout({
  editDocument,
  ready,
  platform,
}: {
  editDocument: EditDocument;
  ready: boolean;
  platform: Platform;
}) {
  const { editor, outline, search } = editDocument;
  // 文件切换时这个组件保持挂载，搜索栏状态随之保留。The component stays mounted across file switches, and so does the
  // find bar's state.
  const [findMemory] = useState(createFindBarMemory);
  const [scroller, setScroller] = useState<HTMLDivElement | null>(null);
  const [documentColumn, setDocumentColumn] = useState<HTMLDivElement | null>(null);
  const headings = useSyncExternalStore(outline.subscribe, () => outline.headings);
  const { active, pin } = useActiveHeading({
    scroller: ready ? scroller : null,
    documentColumn,
    headingElements: () =>
      outline.headings.map(
        (_, index) => editor.view.nodeDOM(outline.position(index)) as HTMLElement | null,
      ),
    headings,
  });

  function selectHeading(index: number): void {
    const pos = outline.position(index);
    editor.commands.focus(pos + 1, { scrollIntoView: false });
    (editor.view.nodeDOM(pos) as HTMLElement).scrollIntoView({ block: "start" });
    pin(index);
  }

  return (
    <div ref={setScroller} className="edit-scroll" hidden={!ready}>
      <div className="edit-layout outline-layout" data-outline="">
        <FindBar target={search} memory={findMemory} platform={platform} enabled={ready} />
        <div ref={setDocumentColumn} className="edit-column document-column">
          <EditorContent editor={editor} className="edit-content" />
        </div>
        <OutlinePanel headings={headings} active={active} onSelect={selectHeading} />
      </div>
    </div>
  );
}

/**
 * 卸载 Mixed 查看器（`view.onUnload`）：先销毁编辑器，再卸载 React。@tiptap/react 的 `EditorContent` 卸载时编辑器若还
 * 没有销毁，会清空 `nodeViews`，ProseMirror 随之重新绘制整个文档。
 * Unmounts the Mixed viewer (`view.onUnload`): destroys the editor first, then unmounts React. When @tiptap/react's
 * `EditorContent` unmounts while the editor is alive, it clears `nodeViews` and ProseMirror draws the whole document
 * again.
 */
export function unmountEditView(root: Root, editDocument: EditDocument): void {
  editDocument.dispose();
  root.unmount();
}
