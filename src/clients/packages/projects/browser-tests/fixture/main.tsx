// FileContent 的浏览器测试页：测试用 `window.fixture.render(scenario)` 渲染一个场景，回调与评论记录在 `window.fixture`。
// 场景带 `editable` 时，Source 编辑一个真实的 Monaco 文本模型，文档的状态来自场景，操作记录在 `calls` 中。
// The FileContent browser test page: tests render a scenario with `window.fixture.render(scenario)`; callbacks and
// comments are recorded on `window.fixture`. With `editable`, Source edits a real Monaco text model whose document
// state comes from the scenario and whose operations are recorded in `calls`.
import "./fixture.css";

import * as React from "react";
import { createRoot } from "react-dom/client";
import { type CodeTextModel, CodeViewAppearanceProvider } from "@agw/code-view";
import { loadCodeTextModel } from "@agw/code-view/text-model";

import type { GitDiffResponse, GitDiffScope } from "../../src/services/files";
import FileContent, {
  type FileViewerOption,
  type FileViewerSaveStatus,
} from "../../src/ui-web/components/file-explorer/file-content";
import type {
  EditableSource,
  SourceDocumentState,
  SourceDocumentStatus,
} from "../../src/ui-web/components/file-explorer/source-document";
import type { LineComment } from "../../src/ui-web/components/file-explorer/types";

export type Handler =
  | "onReloadFileViewer"
  | "onReloadSource"
  | "renderFileViewerInfo"
  | "onReloadFileViewerDocument"
  | "onSetDefaultFileViewer"
  | "onSelectFileViewerItem";

/** 评论在页面与测试之间以 JSON 传递，时间为 ISO 字符串。Comments cross as JSON with ISO timestamps. */
export type CommentData = Omit<LineComment, "timestamp"> & { timestamp: string };

/** 可编辑文档的状态（不含模型与操作）。The editable document's state, without the model and operations. */
export interface EditableScenario {
  status: SourceDocumentStatus;
  saveStatus: FileViewerSaveStatus | null;
  unsaved: boolean;
  encodingLabel: string | null;
  error: { code: string | null; message: string } | null;
}

export interface Scenario {
  selectedFile: string;
  fileContent?: string;
  viewers?: FileViewerOption[];
  handlers?: Handler[];
  defaultFileViewerId?: string | null;
  onlyDiff?: boolean;
  diff?: GitDiffResponse;
  diffScope?: GitDiffScope;
  comments?: CommentData[];
  editable?: EditableScenario;
}

declare global {
  interface Window {
    fixture: {
      render(scenario: Scenario): Promise<void>;
      /** 改变可编辑文档的状态。Changes the editable document's state. */
      setDocument(update: Partial<EditableScenario>): void;
      /** 文本模型的全文。The text model's full text. */
      modelText(): string;
      calls: unknown[][];
      comments: CommentData[];
    };
  }
}

const ENCODINGS = [
  { id: "utf-8", label: "Unicode (UTF-8)" },
  { id: "windows-1252", label: "Western European (Windows 1252)" },
];

const root = createRoot(document.getElementById("root")!);
let generation = 0;
let textModel: CodeTextModel | null = null;
const documentListeners = new Set<(update: Partial<EditableScenario>) => void>();

function toData(comment: LineComment): CommentData {
  return { ...comment, timestamp: comment.timestamp.toISOString() };
}

function record(name: string, ...args: unknown[]): void {
  window.fixture.calls.push([name, ...args]);
}

/** 场景的可编辑 Source：状态可以由测试改变，操作被记录。The scenario's editable Source. */
function useEditableSource(
  scenario: Scenario,
  model: CodeTextModel | null,
): EditableSource | undefined {
  const [state, setState] = React.useState(scenario.editable);
  React.useEffect(() => {
    const listener = (update: Partial<EditableScenario>) =>
      setState((current) => (current === undefined ? current : { ...current, ...update }));
    documentListeners.add(listener);
    return () => {
      documentListeners.delete(listener);
    };
  }, []);
  const document = React.useMemo<SourceDocumentState | null>(
    () =>
      state === undefined || model === null
        ? null
        : {
            ...state,
            model,
            fileName: scenario.selectedFile.split("/").pop()!,
            save: () => record("save"),
            reload: () => record("reload"),
            reloadChanged: () => record("reloadChanged"),
            reopenWithEncoding: (encoding) => record("reopenWithEncoding", encoding),
            saveWithEncoding: (encoding, bom) => record("saveWithEncoding", encoding, bom),
            listEncodings: async () => ENCODINGS,
            isSaveShortcut: (event) =>
              (event.metaKey || event.ctrlKey) &&
              !event.shiftKey &&
              !event.altKey &&
              event.key.toLowerCase() === "s",
            notifyError: (message) => record("notifyError", message),
          },
    [state, model, scenario.selectedFile],
  );
  return React.useMemo(
    () =>
      state === undefined
        ? undefined
        : { document, show: () => record("show", scenario.selectedFile) },
    [state, document, scenario.selectedFile],
  );
}

function App({ scenario, model }: { scenario: Scenario; model: CodeTextModel | null }) {
  const [comments, setComments] = React.useState<LineComment[]>(() =>
    (scenario.comments ?? []).map((comment) => ({
      ...comment,
      timestamp: new Date(comment.timestamp),
    })),
  );
  React.useEffect(() => {
    window.fixture.comments = comments.map(toData);
  }, [comments]);
  const editableSource = useEditableSource(scenario, model);
  const handlers = new Set(scenario.handlers ?? []);
  // 只记录回调声明的参数；菜单的 onSelect 还会传入事件对象。Only the declared callback arguments are recorded; menu
  // onSelect also passes an event object.
  const recordHandler =
    (name: Handler, arity: number) =>
    (...args: unknown[]) => {
      window.fixture.calls.push([name, ...args.slice(0, arity)]);
    };
  return (
    <CodeViewAppearanceProvider revision={0}>
      <div className="flex h-full flex-col">
        <FileContent
          projectId="project-1"
          directoryId="workspace"
          selectedFile={scenario.selectedFile}
          isLoadingContent={false}
          contentError={null}
          onlyDiff={scenario.onlyDiff ?? false}
          diffContentData={scenario.diff ?? null}
          diffScope={scenario.diffScope}
          comments={comments}
          setComments={setComments}
          fileContent={scenario.fileContent ?? "# Title"}
          fileViewers={scenario.viewers}
          renderFileViewer={(viewerId) => <div data-testid="viewer">viewer {viewerId}</div>}
          renderFileViewerInfo={
            handlers.has("renderFileViewerInfo")
              ? (viewerId) => <span data-testid="info">info {viewerId}</span>
              : undefined
          }
          onReloadFileViewer={
            handlers.has("onReloadFileViewer") ? recordHandler("onReloadFileViewer", 1) : undefined
          }
          onReloadSource={
            handlers.has("onReloadSource") ? recordHandler("onReloadSource", 0) : undefined
          }
          onReloadFileViewerDocument={
            handlers.has("onReloadFileViewerDocument")
              ? recordHandler("onReloadFileViewerDocument", 1)
              : undefined
          }
          onSetDefaultFileViewer={
            handlers.has("onSetDefaultFileViewer")
              ? recordHandler("onSetDefaultFileViewer", 1)
              : undefined
          }
          onSelectFileViewerItem={
            handlers.has("onSelectFileViewerItem")
              ? recordHandler("onSelectFileViewerItem", 3)
              : undefined
          }
          defaultFileViewerId={scenario.defaultFileViewerId}
          editableSource={editableSource}
        />
      </div>
    </CodeViewAppearanceProvider>
  );
}

window.fixture = {
  calls: [],
  comments: [],
  async render(scenario) {
    window.fixture.calls = [];
    generation += 1;
    let model: CodeTextModel | null = null;
    if (scenario.editable !== undefined) {
      textModel ??= await loadCodeTextModel();
      // 每个场景重新读入，换成新模型。Every scenario reads its text again into a new model.
      textModel.beginSwitch();
      textModel.load(
        { text: scenario.fileContent ?? "# Title", eol: "\n" },
        { uri: `file:///project/${scenario.selectedFile}`, name: scenario.selectedFile },
      );
      model = textModel;
    }
    root.render(<App key={generation} scenario={scenario} model={model} />);
  },
  setDocument(update) {
    for (const listener of documentListeners) listener(update);
  },
  modelText: () => textModel?.serialize() ?? "",
};
